using System.Buffers.Binary;
using System.IO;
using System.IO.Pipes;
using System.Security.Principal;
using System.Text;
using System.Text.Json;

namespace AltSync.Core;

public sealed record DiscordPresenceStatus(string State, string Message);

public sealed class DiscordPresence : IAsyncDisposable
{
    private readonly object sync = new();
    private readonly SemaphoreSlim changed = new(0, 1);
    private readonly CancellationTokenSource lifetime = new();
    private readonly Func<CancellationToken, Task<Stream>> connect;
    private readonly DiscordReleaseProfile profile;
    private readonly DateTimeOffset started = DateTimeOffset.UtcNow;
    private Task? worker;
    private bool enabled, stopping;
    private DiscordPresenceStatus status = new("disabled", "Off");
    public DiscordPresenceStatus Status => Volatile.Read(ref status);

    public DiscordPresence() : this(DiscordBranding.Profile, ConnectDiscord) { }
    internal DiscordPresence(DiscordReleaseProfile releaseProfile, Func<CancellationToken, Task<Stream>> connector)
    { profile = releaseProfile.Validate(); connect = connector; }

    public void Update(bool showPresence)
    {
        lock (sync)
        {
            if (stopping) return;
            if (enabled == showPresence && worker is not null) return;
            enabled = showPresence;
            if (!profile.IsConfigured) { SetStatus(showPresence ? "unavailable" : "disabled", showPresence ? "Not available in this build" : "Off"); return; }
            worker ??= Task.Run(Run);
            Wake();
        }
    }
    private bool Enabled() { lock (sync) return enabled && !stopping; }
    private bool Stopping() { lock (sync) return stopping; }
    private void Wake() { if (changed.CurrentCount == 0) try { changed.Release(); } catch (SemaphoreFullException) { } }
    private void SetStatus(string state, string message) => Volatile.Write(ref status, new(state, message));

    private async Task Run()
    {
        var token = lifetime.Token;
        try
        {
            while (!Stopping())
            {
                while (changed.Wait(0)) { }
                if (!Enabled()) { SetStatus("disabled", "Off"); await changed.WaitAsync(token); continue; }
                SetStatus("connecting", "Connecting to Discord");
                try
                {
                    await using var stream = await connect(token);
                    using var session = CancellationTokenSource.CreateLinkedTokenSource(token);
                    await using var connection = new DiscordIpc(stream, session);
                    if (!Enabled()) continue;
                    await connection.Handshake(profile.ApplicationId, token);
                    await RunSession(connection, token);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
                catch (DiscordRpcException) { SetStatus("error", "Discord could not display the AltSync presence"); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or TimeoutException or OperationCanceledException or JsonException)
                { SetStatus("waiting", "Waiting for Discord desktop"); }
                if (Enabled()) await changed.WaitAsync(TimeSpan.FromSeconds(15), token);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        finally { SetStatus("disabled", "Off"); }
    }
    private async Task RunSession(DiscordIpc connection, CancellationToken token)
    {
        Task notification = changed.WaitAsync(token);
        try
        {
            if (Enabled())
            {
                await connection.SetActivity(BuildActivity(profile, started), token);
                SetStatus("connected", "Connected to Discord");
            }
            while (Enabled())
            {
                var result = await Task.WhenAny(connection.Incoming, notification);
                token.ThrowIfCancellationRequested();
                if (result == notification) { await notification; notification = changed.WaitAsync(token); }
                else await connection.HandleIncoming(token);
            }
            using var clear = new CancellationTokenSource(TimeSpan.FromSeconds(1));
            try { await connection.SetActivity(null, clear.Token); } catch (Exception ex) when (ex is IOException or TimeoutException or OperationCanceledException or DiscordRpcException or JsonException) { }
        }
        finally
        {
            if (!notification.IsCompleted) { Wake(); await notification; }
        }
    }
    internal static string BuildActivity(DiscordReleaseProfile profile, DateTimeOffset started)
    {
        var activity = new Dictionary<string, object> {
            ["name"] = profile.Name, ["type"] = 0, ["details"] = profile.Details, ["state"] = profile.State,
            ["timestamps"] = new { start = started.ToUnixTimeSeconds() },
            ["assets"] = new { large_image = profile.LargeImage, large_text = profile.Name }
        };
        var buttons = profile.Buttons.Where(button => button.Url.Length > 0).Select(button => new { label = button.Label, url = button.Url }).ToArray();
        if (buttons.Length > 0) activity["buttons"] = buttons;
        return JsonSerializer.Serialize(activity);
    }
    internal static async Task<Stream> ConnectDiscord(CancellationToken token)
    {
        for (int index = 0; index < 10; index++)
        {
            try { return await DiscordPipe.Connect("discord-ipc-" + index, 250, token); }
            catch (Exception ex) when (ex is TimeoutException or IOException or UnauthorizedAccessException) { }
        }
        throw new IOException("Discord desktop is not available.");
    }
    public async ValueTask DisposeAsync()
    {
        Task? running;
        lock (sync) { if (stopping) return; stopping = true; running = worker; Wake(); }
        if (running is not null)
        {
            try { await running.WaitAsync(TimeSpan.FromSeconds(2)); }
            catch (TimeoutException) { lifetime.Cancel(); }
            if (running.IsCompleted) await running;
        }
        lifetime.Cancel();
    }
}

internal sealed class DiscordRpcException : Exception { }
internal sealed record DiscordFrame(int Opcode, byte[] Data);

internal sealed class DiscordIpc(Stream stream, CancellationTokenSource session) : IAsyncDisposable
{
    private const int MaxFrame = 1024 * 1024;
    private Task<DiscordFrame>? incoming;
    public Task<DiscordFrame> Incoming => incoming ??= ReadFrame(stream, session.Token);
    private async Task<DiscordFrame> Take(CancellationToken token)
    {
        var frame = await Incoming.WaitAsync(token); incoming = null; return frame;
    }
    public async Task Handshake(string applicationId, CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token); timeout.CancelAfter(TimeSpan.FromSeconds(5));
        await WriteFrame(stream, 0, Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { v = 1, client_id = applicationId })), timeout.Token);
        while (true)
        {
            var frame = await Take(timeout.Token);
            if (await HandleControl(frame, timeout.Token)) continue;
            using var document = JsonDocument.Parse(frame.Data);
            CheckError(document.RootElement);
            if (document.RootElement.TryGetProperty("evt", out var evt) && evt.GetString() == "READY") return;
            throw new DiscordRpcException();
        }
    }
    public async Task SetActivity(string? activity, CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token); timeout.CancelAfter(TimeSpan.FromSeconds(5));
        string nonce = Guid.NewGuid().ToString("N");
        using var document = activity is null ? null : JsonDocument.Parse(activity);
        var payload = new { cmd = "SET_ACTIVITY", args = new { pid = Environment.ProcessId, activity = document?.RootElement }, nonce };
        await WriteFrame(stream, 1, Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payload)), timeout.Token);
        while (true)
        {
            var frame = await Take(timeout.Token);
            if (await HandleControl(frame, timeout.Token)) continue;
            using var response = JsonDocument.Parse(frame.Data);
            CheckError(response.RootElement);
            if (response.RootElement.TryGetProperty("nonce", out var value) && value.ValueKind == JsonValueKind.String && value.GetString() == nonce) return;
        }
    }
    public async Task HandleIncoming(CancellationToken token)
    {
        var frame = await Take(token);
        if (await HandleControl(frame, token)) return;
        using var document = JsonDocument.Parse(frame.Data); CheckError(document.RootElement);
    }
    private async Task<bool> HandleControl(DiscordFrame frame, CancellationToken token)
    {
        if (frame.Opcode == 3) { await WriteFrame(stream, 4, frame.Data, token); return true; }
        if (frame.Opcode == 4) return true;
        if (frame.Opcode == 2)
        {
            if (frame.Data.Length > 0)
            {
                using var close = JsonDocument.Parse(frame.Data);
                if (close.RootElement.ValueKind == JsonValueKind.Object && close.RootElement.TryGetProperty("code", out var code) && code.ValueKind == JsonValueKind.Number && code.TryGetInt32(out int error) && error is >= 4000 and < 5000) throw new DiscordRpcException();
            }
            throw new IOException("Discord closed the connection.");
        }
        if (frame.Opcode != 1) throw new DiscordRpcException();
        return false;
    }
    private static void CheckError(JsonElement payload)
    {
        if (payload.ValueKind != JsonValueKind.Object) throw new DiscordRpcException();
        if (payload.TryGetProperty("evt", out var evt) && (evt.ValueKind is not (JsonValueKind.Null or JsonValueKind.String) || evt.GetString() == "ERROR")) throw new DiscordRpcException();
    }
    internal static async Task<DiscordFrame> ReadFrame(Stream stream, CancellationToken token)
    {
        byte[] header = new byte[8]; await stream.ReadExactlyAsync(header, token);
        int opcode = BinaryPrimitives.ReadInt32LittleEndian(header), length = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(4));
        if (length < 0 || length > MaxFrame) throw new IOException("Invalid Discord RPC frame length.");
        byte[] body = new byte[length]; await stream.ReadExactlyAsync(body, token); return new(opcode, body);
    }
    internal static async Task WriteFrame(Stream stream, int opcode, byte[] payload, CancellationToken token)
    {
        if (payload.Length > MaxFrame) throw new IOException("Discord RPC payload is too large.");
        byte[] frame = new byte[8 + payload.Length];
        BinaryPrimitives.WriteInt32LittleEndian(frame, opcode); BinaryPrimitives.WriteInt32LittleEndian(frame.AsSpan(4), payload.Length);
        payload.CopyTo(frame, 8); await stream.WriteAsync(frame, token); await stream.FlushAsync(token);
    }
    public async ValueTask DisposeAsync()
    {
        session.Cancel();
        if (incoming is not null) try { await incoming; } catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException) { }
    }
}
