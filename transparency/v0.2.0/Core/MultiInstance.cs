using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace AltSync.Core;

public static class MultiInstance
{
    private static readonly object ScanLock = new();
    [StructLayout(LayoutKind.Sequential)]
    private struct Entry
    {
        public IntPtr Object;
        public UIntPtr Pid, Handle;
        public uint Access;
        public ushort Backtrace, Type;
        public uint Attributes, Reserved;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct UnicodeString { public ushort Length, MaximumLength; public IntPtr Buffer; }
    [DllImport("ntdll.dll")] private static extern int NtQuerySystemInformation(int info, IntPtr buffer, int size, out int needed);
    [DllImport("ntdll.dll")] private static extern int NtQueryObject(IntPtr handle, int info, IntPtr buffer, int size, out int needed);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32.dll")] private static extern IntPtr GetCurrentProcess();
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool DuplicateHandle(IntPtr process, IntPtr handle, IntPtr target, out IntPtr copy, uint access, bool inherit, uint options);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);

    public static int Release() => Release(null);
    public static int Release(int? onlyProcessId)
    {
        lock (ScanLock) return ReleaseCore(onlyProcessId);
    }
    private static int ReleaseCore(int? onlyProcessId)
    {
        using var current = Process.GetCurrentProcess();
        var processes = Process.GetProcessesByName("RobloxPlayerBeta");
        try
        {
            var ids = processes.Where(p => { try { return (onlyProcessId is null || p.Id == onlyProcessId) && p.SessionId == current.SessionId && ProcessOwner.IsCurrentUser(p); } catch { return false; } }).Select(p => p.Id).ToHashSet();
            if (ids.Count == 0) return 0;
            using var marker = new EventWaitHandle(false, EventResetMode.ManualReset);
            IntPtr snapshot = IntPtr.Zero;
            try
            {
                int size = 1 << 20;
                while (true)
                {
                    snapshot = Marshal.AllocHGlobal(size);
                    var status = NtQuerySystemInformation(64, snapshot, size, out int needed);
                    if (status == 0) break;
                    Marshal.FreeHGlobal(snapshot); snapshot = IntPtr.Zero;
                    if (status != unchecked((int)0xC0000004)) throw new InvalidOperationException($"Windows handle enumeration failed (0x{status:X8}).");
                    size = checked(Math.Max(size * 2, needed + 65536));
                    if (size > 256 * 1024 * 1024) throw new InvalidOperationException("Windows handle table exceeded the scan limit.");
                }
                long count = Marshal.ReadIntPtr(snapshot).ToInt64();
                int stride = Marshal.SizeOf<Entry>();
                if (count < 0 || count > (size - IntPtr.Size * 2) / stride) throw new InvalidOperationException("Invalid Windows handle table.");
                ushort eventType = 0;
                var markerHandle = marker.SafeWaitHandle.DangerousGetHandle().ToInt64();
                for (long i = 0; i < count; i++)
                {
                    var e = Marshal.PtrToStructure<Entry>(IntPtr.Add(snapshot, checked(IntPtr.Size * 2 + (int)i * stride)));
                    if (e.Pid.ToUInt64() == (ulong)current.Id && e.Handle.ToUInt64() == (ulong)markerHandle) { eventType = e.Type; break; }
                }
                if (eventType == 0) throw new InvalidOperationException("Unable to identify the Windows Event object type.");
                var candidates = ids.ToDictionary(pid => pid, _ => new List<Entry>());
                for (long i = 0; i < count; i++)
                {
                    var entry = Marshal.PtrToStructure<Entry>(IntPtr.Add(snapshot, checked(IntPtr.Size * 2 + (int)i * stride)));
                    if (entry.Type == eventType && entry.Pid.ToUInt64() <= int.MaxValue && candidates.TryGetValue((int)entry.Pid.ToUInt64(), out var list)) list.Add(entry);
                }
                int closed = 0;
                string expected = $@"\Sessions\{current.SessionId}\BaseNamedObjects\ROBLOX_singletonEvent";
                foreach (int pid in ids)
                {
                    var process = OpenProcess(0x40, false, pid);
                    if (process == IntPtr.Zero) { if (Marshal.GetLastWin32Error() == 5) throw new Win32Exception(5, "Windows denied access to the Roblox process. Multi-instance is unavailable."); continue; }
                    try
                    {
                        foreach (var e in candidates[pid])
                        {
                            var source = new IntPtr(unchecked((long)e.Handle.ToUInt64()));
                            if (!DuplicateHandle(process, source, GetCurrentProcess(), out var copy, 0, false, 2)) continue;
                            bool matches;
                            try { matches = Name(copy, 2) == "Event" && string.Equals(Name(copy, 1), expected, StringComparison.Ordinal); }
                            finally { CloseHandle(copy); }
                            if (!matches) continue;
                            if (DuplicateHandle(process, source, GetCurrentProcess(), out var removed, 0, false, 3)) { CloseHandle(removed); closed++; }
                            else throw new Win32Exception(Marshal.GetLastWin32Error(), "Windows could not release the Roblox singleton event.");
                        }
                    }
                    finally { CloseHandle(process); }
                }
                return closed;
            }
            finally { if (snapshot != IntPtr.Zero) Marshal.FreeHGlobal(snapshot); }
        }
        finally { foreach (var p in processes) p.Dispose(); }
    }
    private static string? Name(IntPtr handle, int info)
    {
        int size = 1024;
        for (int attempt = 0; attempt < 3; attempt++)
        {
            var buffer = Marshal.AllocHGlobal(size);
            try
            {
                int status = NtQueryObject(handle, info, buffer, size, out int needed);
                if (status == 0)
                {
                    var value = Marshal.PtrToStructure<UnicodeString>(buffer);
                    return value.Length == 0 ? null : Marshal.PtrToStringUni(value.Buffer, value.Length / 2);
                }
                if (needed <= size || needed > 65536) return null;
                size = needed;
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }
        return null;
    }
}
