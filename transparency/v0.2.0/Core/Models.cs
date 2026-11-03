namespace AltSync.Core;

public sealed class Account
{
    public long Id { get; set; }
    public string Username { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string Group { get; set; } = "Personal";
    public string Secret { get; set; } = "";
    public string Avatar { get; set; } = "";
    public DateTimeOffset AddedAt { get; set; } = DateTimeOffset.UtcNow;
    public bool Expired { get; set; }
}
public sealed class Preferences
{
    public bool MultiInstance { get; set; } = true;
    public string Performance { get; set; } = "default";
    public string PlaceId { get; set; } = "";
    public string JobId { get; set; } = "";
    public int LaunchDelay { get; set; } = 5;
    public bool AutoApply { get; set; } = true;
    public bool DiscordEnabled { get; set; } = true;
    public bool AutomaticUpdates { get; set; } = true;
    public bool SleepWhenMinimized { get; set; } = true;
    public bool MinimizeToTray { get; set; } = true;
    public bool AdaptiveLaunchDelay { get; set; } = true;
    public bool RestoreWindowPositions { get; set; } = true;
}
public sealed class SavedData
{
    public List<Account> Accounts { get; set; } = [];
    public Preferences Settings { get; set; } = new();
}
public sealed record ActivityEntry(DateTimeOffset Time, string Kind, string Message);
public sealed record SessionInfo(int Pid, long? AccountId, string Name, double MemoryMb, double Cpu, DateTimeOffset? StartedAt);
