using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace AltSync.Core;

internal static class DiscordPipe
{
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint processId);

    internal static async Task<Stream> Connect(string name, int timeout, CancellationToken token)
    {
        // CurrentUserOnly compares token owners, which differ between an elevated
        // AltSync and unelevated Discord. Check the actual server user's identity instead.
        var pipe = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous, TokenImpersonationLevel.Identification);
        try
        {
            await pipe.ConnectAsync(timeout, token);
            if (!GetNamedPipeServerProcessId(pipe.SafePipeHandle, out uint processId) || !IsCurrentUserSession(processId))
                throw new UnauthorizedAccessException("Discord IPC does not belong to the current Windows user and desktop session.");
            return pipe;
        }
        catch { pipe.Dispose(); throw; }
    }

    internal static bool IsCurrentUserSession(uint processId)
    {
        if (processId == 0 || processId > int.MaxValue) return false;
        try
        {
            using var server = Process.GetProcessById((int)processId);
            using var current = Process.GetCurrentProcess();
            return server.SessionId == current.SessionId && ProcessOwner.IsCurrentUser(server);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { return false; }
    }
}
