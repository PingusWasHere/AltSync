using System.Diagnostics;

namespace AltSync.Core;

public sealed class SessionMonitor
{
    private readonly Dictionary<int, (DateTime Start, double Cpu, DateTime Sample)> previous = [];
    private readonly Dictionary<int, (long Account, string Name, DateTime Start)> ownership = [];
    public void Track(Process process, Account account) => ownership[process.Id] = (account.Id, account.DisplayName, process.StartTime.ToUniversalTime());
    public List<SessionInfo> Snapshot()
    {
        List<SessionInfo> result = [];
        using var self = Process.GetCurrentProcess();
        foreach (var process in Process.GetProcessesByName("RobloxPlayerBeta"))
        {
            using (process)
            {
                try
                {
                    if (process.SessionId != self.SessionId || !ProcessOwner.IsCurrentUser(process)) continue;
                    var now = DateTime.UtcNow;
                    var started = process.StartTime.ToUniversalTime();
                    double cpu = process.TotalProcessorTime.TotalMilliseconds;
                    double percent = 0;
                    if (previous.TryGetValue(process.Id, out var sample) && sample.Start == started)
                        percent = Math.Clamp((cpu - sample.Cpu) / Math.Max(1, (now - sample.Sample).TotalMilliseconds) / Environment.ProcessorCount * 100, 0, 100);
                    previous[process.Id] = (started, cpu, now);
                    bool known = ownership.TryGetValue(process.Id, out var owner) && owner.Start == started;
                    result.Add(new(process.Id, known ? owner.Account : null, known ? owner.Name : "External Roblox session", Math.Round(process.WorkingSet64 / 1048576d, 1), Math.Round(percent, 1), new DateTimeOffset(started)));
                }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException) { }
            }
        }
        var alive = result.Select(r => r.Pid).ToHashSet();
        foreach (int pid in previous.Keys.Where(k => !alive.Contains(k)).ToArray()) { previous.Remove(pid); ownership.Remove(pid); }
        return result;
    }
    public void Stop(int pid)
    {
        using var process=Owned(pid);process.Kill();
    }
    public Process Owned(int pid)
    {
        if (!ownership.TryGetValue(pid, out var owner)) throw new InvalidOperationException("Only sessions launched by this AltSync window can be stopped here.");
        var process = Process.GetProcessById(pid);
        if (process.ProcessName != "RobloxPlayerBeta" || process.StartTime.ToUniversalTime() != owner.Start || !ProcessOwner.IsCurrentUser(process)) { process.Dispose();throw new InvalidOperationException("The original Roblox session has already ended."); }
        return process;
    }
}
