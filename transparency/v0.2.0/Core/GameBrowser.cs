using System.Net;
using System.Net.Http;
using System.Text.Json;

namespace AltSync.Core;

public sealed record BrowseGame(string UniverseId, string PlaceId, string Name, string Creator, long Players, string Icon);
public sealed record GamePage(IReadOnlyList<BrowseGame> Games, string NextPageToken);

public sealed class GameBrowser : IDisposable
{
    private readonly HttpClient client;
    private readonly string sessionId = Guid.NewGuid().ToString();
    private readonly Dictionary<string, (DateTimeOffset Time, GamePage Page)> cache = [];
    private readonly Dictionary<string, (DateTimeOffset Time, BrowseGame Game)> details = [];
    public GameBrowser(HttpClient? client = null)
    {
        // Discovery is public and never shares the account API's cookie jar.
        this.client = client ?? new HttpClient(new HttpClientHandler { UseCookies = false, AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(15) };
        this.client.DefaultRequestHeaders.UserAgent.ParseAdd("AltSync/0.1");
    }
    public async Task<GamePage> Search(string query, string pageToken = "", CancellationToken token = default)
    {
        query = query.Trim();
        token.ThrowIfCancellationRequested();
        if (query.Length > 80 || query.Any(char.IsControl) || pageToken.Length > 2048 || pageToken.Any(char.IsControl) || query.Length == 0 && pageToken.Length > 0)
            throw new ArgumentException("Use a game name of up to 80 characters.");
        string cacheKey = query + "\n" + pageToken;
        if (cache.TryGetValue(cacheKey, out var cached) && DateTimeOffset.UtcNow - cached.Time < TimeSpan.FromSeconds(45)) return cached.Page;
        string url = query.Length == 0
            ? $"https://apis.roblox.com/explore-api/v1/get-sort-content?sessionId={sessionId}&sortId=top-playing-now&device=computer&country=all"
            : $"https://apis.roblox.com/search-api/omni-search?searchQuery={Uri.EscapeDataString(query)}&sessionId={sessionId}&pageToken={Uri.EscapeDataString(pageToken)}";
        using var document = await GetJson(url, token);
        var root = document.RootElement;
        IEnumerable<JsonElement> entries;
        if (query.Length == 0 && root.TryGetProperty("games", out var games) && games.ValueKind == JsonValueKind.Array) entries = games.EnumerateArray().ToArray();
        else if (query.Length > 0 && root.TryGetProperty("searchResults", out var groups) && groups.ValueKind == JsonValueKind.Array)
            entries = groups.EnumerateArray().Where(group => String(group, "contentGroupType") == "Game" && group.TryGetProperty("contents", out var items) && items.ValueKind == JsonValueKind.Array).SelectMany(group => group.GetProperty("contents").EnumerateArray().ToArray());
        else throw new InvalidOperationException("Roblox returned an unexpected game list. Try searching again.");
        var seen = new HashSet<string>();
        var found = new List<BrowseGame>();
        foreach (var entry in entries)
        {
            if (!Id(entry, "universeId", out var universe) || !Id(entry, "rootPlaceId", out var place)) continue;
            string name = String(entry, "name");
            if (string.IsNullOrWhiteSpace(name) || name.Length > 200 || !seen.Add(universe)) continue;
            long players = entry.TryGetProperty("playerCount", out var count) && count.ValueKind == JsonValueKind.Number && count.TryGetInt64(out long value) ? Math.Max(0, value) : 0;
            found.Add(new(universe, place, name, String(entry, "creatorName"), players, ""));
            if (found.Count == 40) break;
        }
        if (found.Count > 0)
        {
            try
            {
                using var icons = await GetJson("https://thumbnails.roblox.com/v1/games/icons?universeIds=" + string.Join(",", found.Select(game => game.UniverseId)) + "&returnPolicy=PlaceHolder&size=150x150&format=Png&isCircular=false", token);
                if (icons.RootElement.TryGetProperty("data", out var items) && items.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in items.EnumerateArray())
                    {
                        if (!Id(item, "targetId", out var id) || !ImageUrl(String(item, "imageUrl"), out var image)) continue;
                        int index = found.FindIndex(game => game.UniverseId == id);
                        if (index >= 0) found[index] = found[index] with { Icon = image };
                    }
                }
            }
            catch (Exception ex) when (!token.IsCancellationRequested && ex is HttpRequestException or OperationCanceledException or JsonException or InvalidOperationException) { /* Artwork is optional. */ }
        }
        token.ThrowIfCancellationRequested();
        string next = query.Length > 0 ? String(root, "nextPageToken") : "";
        if (next.Length > 2048 || next.Any(char.IsControl) || next == pageToken) next = "";
        var page = new GamePage(found.ToArray(), next);
        foreach (var game in found) Remember(game);
        if (cache.Count >= 16) cache.Remove(cache.MinBy(item => item.Value.Time).Key);
        cache[cacheKey] = (DateTimeOffset.UtcNow, page);
        return page;
    }
    public BrowseGame? Cached(string placeId) => details.TryGetValue(placeId, out var saved) ? saved.Game : null;
    private void Remember(BrowseGame game)
    {
        if (details.Count >= 150 && !details.ContainsKey(game.PlaceId)) details.Remove(details.MinBy(item => item.Value.Time).Key);
        details[game.PlaceId] = (DateTimeOffset.UtcNow, game);
    }
    public async Task<BrowseGame> Details(string placeId, CancellationToken token = default)
    {
        if (!long.TryParse(placeId, out long id) || id <= 0 || id.ToString(System.Globalization.CultureInfo.InvariantCulture) != placeId) throw new ArgumentException("Use a valid place ID.");
        if (details.TryGetValue(placeId, out var saved) && DateTimeOffset.UtcNow - saved.Time < TimeSpan.FromMinutes(15)) return saved.Game;
        using var universeData = await GetJson($"https://apis.roblox.com/universes/v1/places/{placeId}/universe", token);
        if (!Id(universeData.RootElement, "universeId", out string universeId)) throw new InvalidOperationException("This Roblox game was not found.");
        using var gameData = await GetJson($"https://games.roblox.com/v1/games?universeIds={universeId}", token);
        var items = gameData.RootElement.GetProperty("data");
        if (items.GetArrayLength() != 1) throw new InvalidOperationException("This Roblox game was not found.");
        var item = items[0];
        string name = String(item, "name");
        if (string.IsNullOrWhiteSpace(name) || name.Length > 200) throw new InvalidOperationException("Roblox returned an invalid game name.");
        var creator = item.TryGetProperty("creator", out var author) ? String(author, "name") : "";
        long players = item.TryGetProperty("playing", out var online) && online.TryGetInt64(out long count) ? Math.Max(0, count) : 0;
        var game = new BrowseGame(universeId, placeId, name, creator.Length > 200 ? creator[..200] : creator, players, saved.Game?.Icon ?? "");
        try
        {
            using var icons = await GetJson($"https://thumbnails.roblox.com/v1/games/icons?universeIds={universeId}&returnPolicy=PlaceHolder&size=150x150&format=Png&isCircular=false", token);
            foreach (var icon in icons.RootElement.GetProperty("data").EnumerateArray())
                if (Id(icon, "targetId", out var target) && target == universeId && ImageUrl(String(icon, "imageUrl"), out string url)) game = game with { Icon = url };
        }
        catch (Exception ex) when (!token.IsCancellationRequested && ex is HttpRequestException or OperationCanceledException or JsonException or InvalidOperationException) { }
        Remember(game); return game;
    }
    private async Task<JsonDocument> GetJson(string url, CancellationToken token)
    {
        using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, token);
        if (response.StatusCode == HttpStatusCode.TooManyRequests) throw new InvalidOperationException("Roblox is receiving too many searches. Wait a moment and try again.");
        if (!response.IsSuccessStatusCode) throw new HttpRequestException("Roblox game discovery is temporarily unavailable.");
        if (response.Content.Headers.ContentLength > 2 * 1024 * 1024) throw new InvalidOperationException("Roblox returned an oversized game list.");
        // Bound chunked responses as well as responses with Content-Length.
        await using var input = await response.Content.ReadAsStreamAsync(token);
        using var buffer = new System.IO.MemoryStream();
        byte[] chunk = new byte[16384];
        int read;
        while ((read = await input.ReadAsync(chunk, token)) > 0)
        {
            if (buffer.Length + read > 2 * 1024 * 1024) throw new InvalidOperationException("Roblox returned an oversized game list.");
            buffer.Write(chunk, 0, read);
        }
        return JsonDocument.Parse(buffer.ToArray());
    }
    private static string String(JsonElement node, string name) => node.ValueKind == JsonValueKind.Object && node.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";
    private static bool Id(JsonElement node, string name, out string id)
    {
        id = "";
        if (node.ValueKind != JsonValueKind.Object || !node.TryGetProperty(name, out var value)) return false;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out long number) && number > 0) { id = number.ToString(System.Globalization.CultureInfo.InvariantCulture); return true; }
        if (value.ValueKind == JsonValueKind.String && long.TryParse(value.GetString(), out number) && number > 0) { id = number.ToString(System.Globalization.CultureInfo.InvariantCulture); return true; }
        return false;
    }
    internal static bool ImageUrl(string input, out string url)
    {
        url = "";
        if (input.Length > 1024 || !Uri.TryCreate(input, UriKind.Absolute, out var uri) || uri.Scheme != "https" || !uri.Host.EndsWith(".rbxcdn.com", StringComparison.OrdinalIgnoreCase) || uri.UserInfo.Length > 0 || !uri.IsDefaultPort) return false;
        url = uri.AbsoluteUri; return true;
    }
    public void Dispose() => client.Dispose();
}
