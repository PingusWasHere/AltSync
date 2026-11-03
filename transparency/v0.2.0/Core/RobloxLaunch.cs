namespace AltSync.Core;
public static class RobloxLaunch
{
    public static string Uri(string ticket,ResolvedLaunchDestination? destination,long time)
    {
        if(string.IsNullOrWhiteSpace(ticket)||ticket.Length>8192||ticket.Any(c=>char.IsControl(c)||c is '+' or ':' or ' '))throw new InvalidOperationException("Roblox returned an invalid launch ticket.");
        string mode=destination is null?"app":"play";
        return $"roblox-player:1+launchmode:{mode}+gameinfo:{ticket}+launchtime:{time}"+(destination is null?"":"+placelauncherurl:"+System.Uri.EscapeDataString(destination.LauncherUrl()))+"+robloxLocale:en_us+gameLocale:en_us+channel:";
    }
}
