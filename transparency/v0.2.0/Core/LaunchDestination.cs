using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;

namespace AltSync.Core;

public sealed record LaunchDestination(long PlaceId, string LinkCode, string ShareCode, string Value)
{
    public bool IsPrivate => LinkCode.Length > 0 || ShareCode.Length > 0;
    public const int MaxLength = 2048;

    public static LaunchDestination Parse(string value)
    {
        value = value.Trim();
        if (value.Length > MaxLength) throw Invalid();
        if (IsPlaceId(value, out long placeId)) return new(placeId, "", "", value);
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || !IsRobloxUrl(uri)) throw Invalid();
        var query = Query(uri);
        if (uri.AbsolutePath.TrimEnd('/') == "/share")
        {
            if (!query.TryGetValue("code", out string? code) || !Regex.IsMatch(code, "^[a-fA-F0-9]{32}$") ||
                !query.TryGetValue("type", out string? type) || !type.Equals("Server", StringComparison.OrdinalIgnoreCase)) throw Invalid();
            return new(0, "", code, $"https://www.roblox.com/share?code={code}&type=Server");
        }
        var match = Regex.Match(uri.AbsolutePath, @"^/games/([1-9]\d{0,18})(?:/|$)");
        if (!match.Success || !IsPlaceId(match.Groups[1].Value, out placeId)) throw Invalid();
        if (query.ContainsKey("accessCode") || query.ContainsKey("linkCode")) throw Invalid();
        if (query.TryGetValue("privateServerLinkCode", out string? link))
        {
            if (!ValidLinkCode(link)) throw Invalid();
            return new(placeId, link, "", $"https://www.roblox.com/games/{placeId}?privateServerLinkCode={Uri.EscapeDataString(link)}");
        }
        return new(placeId, "", "", placeId.ToString(CultureInfo.InvariantCulture));
    }

    internal static bool IsRobloxUrl(Uri uri) => uri.Scheme == "https" && uri.IsDefaultPort && uri.UserInfo.Length == 0 &&
        (uri.Host is "roblox.com" or "www.roblox.com" or "web.roblox.com");
    internal static bool ValidLinkCode(string code) => Regex.IsMatch(code, "^[a-zA-Z0-9_-]{1,256}$");
    internal static bool IsPlaceId(string value, out long placeId) => long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out placeId) && placeId > 0 && value[0] != '0';
    private static Dictionary<string, string> Query(Uri uri)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string item in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var pair = item.Split('=', 2);
            string key = WebUtility.UrlDecode(pair[0]);
            if (!result.TryAdd(key, pair.Length == 2 ? WebUtility.UrlDecode(pair[1]) : "")) throw Invalid();
        }
        return result;
    }
    private static ArgumentException Invalid() => new("Enter a place ID, a Roblox game link, or a valid private server link.");
}

public sealed record ResolvedLaunchDestination(long PlaceId, string JobId = "", string AccessCode = "", string LinkCode = "")
{
    public bool IsPrivate => AccessCode.Length > 0;
    public string LauncherUrl()
    {
        if (PlaceId <= 0 || (IsPrivate && !Guid.TryParse(AccessCode, out _)) ||
            (LinkCode.Length > 0 && (!IsPrivate || !LaunchDestination.ValidLinkCode(LinkCode))) ||
            (JobId.Length > 0 && (IsPrivate || !Guid.TryParse(JobId, out _)))) throw new ArgumentException("Invalid resolved Roblox destination.");
        string request = IsPrivate ? "RequestPrivateGame" : JobId.Length > 0 ? "RequestGameJob" : "RequestGame";
        string url = $"https://assetgame.roblox.com/game/PlaceLauncher.ashx?request={request}&placeId={PlaceId}&isPlayTogetherGame=false";
        if (IsPrivate) url += "&accessCode=" + Uri.EscapeDataString(AccessCode) + (LinkCode.Length > 0 ? "&linkCode=" + Uri.EscapeDataString(LinkCode) : "");
        else if (JobId.Length > 0) url += "&gameId=" + Uri.EscapeDataString(JobId);
        return url;
    }
}
