using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AltSync.Core;

public sealed record AutoUpdateStatus(string State, string Message, string Version = "");
internal sealed record PublishedUpdate(string Version, string Url, string Sha256, long Size);

public sealed class AutoUpdater : IDisposable
{
    private const string Repository = "https://github.com/PingusWasHere/AltSync";
    private const long MaximumDownload = 256L * 1024 * 1024;
    private readonly HttpClient client;
    private readonly string installRoot, workRoot;
    private readonly Version current;
    private readonly bool supported, secureStorage;
    private int checking;
    private DateTimeOffset nextCheck;
    private AutoUpdateStatus status = new("waiting", "Updates download automatically and install when AltSync next opens.");
    public AutoUpdateStatus Status => Volatile.Read(ref status);
    public AutoUpdater(bool preview = false) : this(AppContext.BaseDirectory, WorkFolder(AppContext.BaseDirectory), RunningVersion(), new HttpClient { Timeout = TimeSpan.FromSeconds(20) }, !preview && IsPortableAdministrator(), true) { }
    internal AutoUpdater(string installRoot, string workRoot, Version current, HttpClient client, bool supported, bool secureStorage = false)
    {
        this.installRoot = Path.GetFullPath(installRoot).TrimEnd('\\'); this.workRoot = workRoot; this.current = current; this.client = client; this.supported = supported; this.secureStorage = secureStorage;
        client.DefaultRequestHeaders.UserAgent.ParseAdd("AltSync-Updater/1.0");
        if (!supported) Set("disabled", "Automatic updates are available in the installed administrator build.");
    }
    private void Set(string state, string message, string version = "") => Volatile.Write(ref status, new(state, message, version));
    public void Pulse(bool enabled, CancellationToken token)
    {
        if (!supported || Volatile.Read(ref checking) != 0) return;
        if (!enabled) { Set("disabled", "Automatic updates are off."); return; }
        if (DateTimeOffset.UtcNow >= nextCheck) _ = Check(enabled, token);
    }
    public async Task Check(bool download, CancellationToken token)
    {
        if (!supported || Interlocked.CompareExchange(ref checking, 1, 0) != 0) return;
        nextCheck = DateTimeOffset.UtcNow.AddMinutes(30);
        string? jobFolder = null;
        bool staged = false;
        try
        {
            Set("checking", "Checking for updates…");
            using var response = await client.GetAsync("https://api.github.com/repos/PingusWasHere/AltSync/releases/latest", token);
            if (!response.IsSuccessStatusCode) throw new HttpRequestException();
            string json = await response.Content.ReadAsStringAsync(token);
            if (json.Length > 2 * 1024 * 1024) throw new InvalidDataException();
            using var document = JsonDocument.Parse(json);
            var release = ParseRelease(document.RootElement);
            if (ParseVersion(release.Version) <= current) { Set("current", "You are using the latest version."); return; }
            if (!download) { Set("available", "A newer version is available. Enable automatic updates to download it.", release.Version); return; }
            if (secureStorage) ProtectRoot(); else Directory.CreateDirectory(workRoot);
            string pendingPath = Path.Combine(workRoot, "pending.json");
            if (File.Exists(pendingPath))
            {
                var previous = JsonSerializer.Deserialize<UpdatePlan>(await File.ReadAllTextAsync(pendingPath, token), Vault.Json);
                if (previous?.Version == release.Version) { Set("ready", "Update downloaded. It will install when AltSync next opens.", release.Version); return; }
            }
            string resultPath = Path.Combine(workRoot, "result.json");
            if (File.Exists(resultPath))
            {
                using var result = JsonDocument.Parse(await File.ReadAllTextAsync(resultPath, token));
                var root = result.RootElement;
                if (root.TryGetProperty("success", out var success) && !success.GetBoolean() && root.GetProperty("version").GetString() == release.Version && DateTimeOffset.TryParse(root.GetProperty("time").GetString(), out var failedAt) && DateTimeOffset.UtcNow - failedAt < TimeSpan.FromHours(12))
                { Set("error", "The previous update could not be installed. Please retry later.", release.Version); return; }
            }
            string job = Path.Combine(workRoot, Guid.NewGuid().ToString("N")); Directory.CreateDirectory(job); jobFolder = job;
            string zip = Path.Combine(job, "update.zip"), stage = Path.Combine(job, "stage");
            Set("downloading", "Downloading update…", release.Version);
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(token))
            {
                timeout.CancelAfter(TimeSpan.FromMinutes(5));
                using var downloadResponse = await client.GetAsync(release.Url, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
                downloadResponse.EnsureSuccessStatusCode();
                if (downloadResponse.Content.Headers.ContentLength is long length && length != release.Size) throw new InvalidDataException("Update size differs.");
                await using var input = await downloadResponse.Content.ReadAsStreamAsync(timeout.Token);
                await using var output = File.Create(zip);
                byte[] buffer = new byte[81920]; long received = 0; int read;
                while ((read = await input.ReadAsync(buffer, timeout.Token)) > 0)
                {
                    received += read;
                    if (received > release.Size || received > MaximumDownload) throw new InvalidDataException("Update is too large.");
                    await output.WriteAsync(buffer.AsMemory(0, read), timeout.Token);
                    Set("downloading", $"Downloading update… {received * 100 / release.Size}%", release.Version);
                }
                if (received != release.Size) throw new InvalidDataException("Update download is incomplete.");
            }
            await using (var input = File.OpenRead(zip))
                if (!Convert.ToHexString(await SHA256.HashDataAsync(input, token)).Equals(release.Sha256, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Update checksum differs.");
            Set("verifying", "Verifying the downloaded update…", release.Version);
            var files = await Task.Run(() => UpdatePackage.Extract(zip, stage, token), token);
            string executableVersion = FileVersionInfo.GetVersionInfo(Path.Combine(stage, "AltSync.exe")).ProductVersion ?? "";
            if (ParseVersion(executableVersion.Split('+')[0]) != ParseVersion(release.Version)) throw new InvalidDataException("Update executable has a different version.");
            var plan = new UpdatePlan(installRoot, workRoot, stage, release.Version, files);
            await File.WriteAllTextAsync(pendingPath + ".tmp", JsonSerializer.Serialize(plan, Vault.Json), token);
            File.Move(pendingPath + ".tmp", pendingPath, true);
            staged = true; File.Delete(zip);
            Set("ready", "Update downloaded. It will install when AltSync next opens.", release.Version);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception) { Set("error", "Could not prepare the update. AltSync will try again later."); }
        finally
        {
            if (!staged && jobFolder is not null)
            {
                // Only this newly created updater-owned GUID directory may be removed.
                string target = Path.GetFullPath(jobFolder);
                if (Path.GetDirectoryName(target) == Path.GetFullPath(workRoot) && Regex.IsMatch(Path.GetFileName(target), "^[a-f0-9]{32}$") && Directory.Exists(target))
                    try { Directory.Delete(target, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
            Volatile.Write(ref checking, 0);
        }
    }
    internal static PublishedUpdate ParseRelease(JsonElement root)
    {
        if (root.GetProperty("draft").GetBoolean() || root.GetProperty("prerelease").GetBoolean()) throw new InvalidDataException("Not a stable release.");
        string version = root.GetProperty("tag_name").GetString() ?? ""; ParseVersion(version);
        var assets = root.GetProperty("assets").EnumerateArray().Where(item => item.GetProperty("name").GetString() == "AltSync-win-x64.zip").ToArray();
        if (assets.Length != 1) throw new InvalidDataException("Update archive is missing.");
        var asset = assets[0];
        string url = asset.GetProperty("browser_download_url").GetString() ?? "", digest = asset.GetProperty("digest").GetString() ?? "";
        long size = asset.GetProperty("size").GetInt64();
        if (url != $"{Repository}/releases/download/{version}/AltSync-win-x64.zip" || !Regex.IsMatch(digest, "^sha256:[a-fA-F0-9]{64}$") || size is <= 0 or > MaximumDownload) throw new InvalidDataException("Invalid update metadata.");
        return new(version, url, digest[7..], size);
    }
    internal static Version ParseVersion(string input)
    {
        if (!Regex.IsMatch(input, "^v?[0-9]+\\.[0-9]+\\.[0-9]+(?:\\.[0-9]+)?$") || !Version.TryParse(input.TrimStart('v'), out var version)) throw new InvalidDataException("Invalid update version.");
        return new(version.Major, version.Minor, version.Build);
    }
    private static Version RunningVersion() { var value = typeof(AutoUpdater).Assembly.GetName().Version!; return new(value.Major, value.Minor, value.Build); }
    private static bool IsPortableAdministrator() => File.Exists(Path.Combine(AppContext.BaseDirectory, "coreclr.dll")) && new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);
    private static string WorkFolder(string installRoot)
    {
        string key = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(Path.GetFullPath(installRoot).TrimEnd('\\').ToUpperInvariant() + WindowsIdentity.GetCurrent().User!.Value)))[..24];
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "AltSyncUpdates", key);
    }
    private void ProtectRoot()
    {
        string root = Path.GetDirectoryName(workRoot)!;
        var admins = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        var security = new DirectorySecurity(); security.SetAccessRuleProtection(true, false); security.SetOwner(admins);
        foreach (var identity in new[] { admins, system }) security.AddAccessRule(new(identity, FileSystemRights.FullControl, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        var directory = new DirectoryInfo(root); directory.Create(security);
        var actual = directory.GetAccessControl();
        if ((directory.Attributes & FileAttributes.ReparsePoint) != 0 || !new[] { admins.Value, system.Value }.Contains(actual.GetOwner(typeof(SecurityIdentifier))?.Value ?? "")) throw new InvalidDataException("The update storage is not trusted.");
        foreach (FileSystemAccessRule rule in actual.GetAccessRules(true, true, typeof(SecurityIdentifier)))
            if (rule.AccessControlType == AccessControlType.Allow && (rule.FileSystemRights & (FileSystemRights.Write | FileSystemRights.Delete | FileSystemRights.ChangePermissions)) != 0 && !new[] { admins.Value, system.Value }.Contains(rule.IdentityReference.Value)) throw new InvalidDataException("The update storage is writable by an untrusted account.");
        Directory.CreateDirectory(workRoot);
        if ((File.GetAttributes(workRoot) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("The update storage is redirected.");
    }
    public static bool TryApplyPending()
    {
        if (!IsPortableAdministrator()) return false;
        string installRoot = Path.GetFullPath(AppContext.BaseDirectory).TrimEnd('\\');
        string workRoot = WorkFolder(installRoot), pending = Path.Combine(workRoot, "pending.json");
        if (!File.Exists(pending)) return false;
        try
        {
            using var checker = new AutoUpdater(); checker.ProtectRoot();
            // Respect the visibility switch saved by the previously running app.
            string vaultPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AltSync", "accounts.json");
            if (File.Exists(vaultPath))
            {
                using var vault = JsonDocument.Parse(File.ReadAllText(vaultPath));
                if (vault.RootElement.TryGetProperty("settings", out var settings) && settings.TryGetProperty("automaticUpdates", out var enabled) && enabled.ValueKind == JsonValueKind.False) return false;
            }
            var plan = JsonSerializer.Deserialize<UpdatePlan>(File.ReadAllText(pending), Vault.Json) ?? throw new InvalidDataException();
            if (plan.InstallRoot != installRoot || plan.WorkRoot != workRoot || !Path.GetFullPath(plan.StageRoot).StartsWith(workRoot + "\\", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException();
            if (ParseVersion(plan.Version) <= RunningVersion()) { File.Delete(pending); return false; }
            foreach (var (name, hash) in plan.Files)
            {
                using var input = File.OpenRead(UpdatePackage.Inside(plan.StageRoot, name));
                if (!Convert.ToHexString(SHA256.HashData(input)).Equals(hash, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException();
            }
            string job = Path.GetDirectoryName(plan.StageRoot)!;
            string script = Path.Combine(job, "install.ps1"), planPath = Path.Combine(job, "install.json");
            using var resource = typeof(AutoUpdater).Assembly.GetManifestResourceStream("AltSync.Updater.Install.ps1") ?? throw new InvalidDataException();
            using (var reader = new StreamReader(resource)) File.WriteAllText(script, reader.ReadToEnd());
            File.WriteAllText(planPath, JsonSerializer.Serialize(plan with { ParentId = Environment.ProcessId }, Vault.Json));
            var start = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe")) { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden };
            start.Environment.Remove("PSModulePath");
            foreach (string argument in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", script, "-PlanPath", planPath }) start.ArgumentList.Add(argument);
            using var process = Process.Start(start) ?? throw new IOException("The update installer could not start."); return true;
        }
        catch { try { File.Delete(pending); } catch (IOException) { } catch (UnauthorizedAccessException) { } return false; }
    }
    public void Dispose() => client.Dispose();
}
