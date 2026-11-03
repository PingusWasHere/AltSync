using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;

namespace AltSync.Core;

internal static class ProcessOwner
{
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
    public static bool IsCurrentUser(Process process)
    {
        var handle = OpenProcess(0x1000, false, process.Id);
        if (handle == IntPtr.Zero) return false;
        try
        {
            if (!OpenProcessToken(handle, 8, out var token)) return false;
            try
            {
                using var owner = new WindowsIdentity(token);
                using var current = WindowsIdentity.GetCurrent();
                return owner.User is not null && owner.User == current.User;
            }
            finally { CloseHandle(token); }
        }
        finally { CloseHandle(handle); }
    }
}
