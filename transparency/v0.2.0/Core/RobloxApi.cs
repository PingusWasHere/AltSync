using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text;
using System.Text.RegularExpressions;

namespace AltSync.Core;

public sealed class RobloxApi
{
    private readonly Func<string, HttpClient> createClient;
    public RobloxApi(Func<string, HttpClient>? clientFactory = null) => createClient = clientFactory ?? Client;
    private static HttpClient Client(string cookie)
    {
        var jar = new CookieContainer();
        jar.Add(new Cookie(".ROBLOSECURITY", cookie, "/", ".roblox.com") { Secure = true, HttpOnly = true });
        var client = new HttpClient(new HttpClientHandler { CookieContainer = jar, AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(25) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("AltSync/0.1");
        return client;
    }
    public async Task<Account> Validate(string cookie, CancellationToken token = default)
    {
        using var client = createClient(cookie);
        using var response = await client.GetAsync("https://users.roblox.com/v1/users/authenticated", token);
        if (response.StatusCode == HttpStatusCode.Unauthorized) throw new InvalidOperationException("This Roblox session has expired. Sign in again.");
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
        var user = doc.RootElement;
        var account = new Account { Id = user.GetProperty("id").GetInt64(), Username = user.GetProperty("name").GetString()!, DisplayName = user.GetProperty("displayName").GetString()!, Secret = Vault.Protect(cookie) };
        try
        {
            using var thumb = await client.GetAsync($"https://thumbnails.roblox.com/v1/users/avatar-headshot?userIds={account.Id}&size=150x150&format=Png&isCircular=false", token);
            thumb.EnsureSuccessStatusCode();
            using var image = JsonDocument.Parse(await thumb.Content.ReadAsStringAsync(token));
            account.Avatar = image.RootElement.GetProperty("data")[0].GetProperty("imageUrl").GetString() ?? "";
        }
        catch (Exception ex) when (!token.IsCancellationRequested && ex is HttpRequestException or JsonException or TaskCanceledException or KeyNotFoundException) { /* Avatar is optional. */ }
        token.ThrowIfCancellationRequested();
        return account;
    }
    public async Task<string> Ticket(Account account,CancellationToken token=default)
    {
        using var client = createClient(Vault.Unprotect(account.Secret));
        client.DefaultRequestHeaders.Referrer = new Uri("https://www.roblox.com/");
        client.DefaultRequestHeaders.Add("Origin", "https://www.roblox.com");
        using var initial = await client.PostAsync("https://auth.roblox.com/v1/authentication-ticket", new StringContent(""),token);
        if (initial.IsSuccessStatusCode && initial.Headers.TryGetValues("rbx-authentication-ticket", out var first)) return first.First();
        if (!initial.Headers.TryGetValues("x-csrf-token", out var tokens)) throw new InvalidOperationException("Roblox did not issue a launch token. Sign in again or retry later.");
        client.DefaultRequestHeaders.Add("x-csrf-token", tokens.First());
        using var response = await client.PostAsync("https://auth.roblox.com/v1/authentication-ticket", new StringContent(""),token);
        if (response.StatusCode == HttpStatusCode.Unauthorized) { account.Expired = true; throw new InvalidOperationException("Session expired. Sign in to this account again."); }
        if (!response.IsSuccessStatusCode || !response.Headers.TryGetValues("rbx-authentication-ticket", out var ticket)) throw new InvalidOperationException($"Roblox rejected the launch request (HTTP {(int)response.StatusCode}). Sign in again if Roblox requires a challenge.");
        return ticket.First();
    }

    public async Task<ResolvedLaunchDestination> ResolveDestination(Account account, LaunchDestination destination, string jobId,CancellationToken token=default)
    {
        if (!destination.IsPrivate)
        {
            if (jobId.Length > 0 && !Guid.TryParse(jobId, out _)) throw new ArgumentException("Server instance ID must be a valid UUID, or leave it empty.");
            return new(destination.PlaceId, jobId);
        }
        if (account.Expired) throw new InvalidOperationException("Session expired. Sign in to this account again.");
        using var client = createClient(Vault.Unprotect(account.Secret));
        client.DefaultRequestHeaders.Referrer = new Uri("https://www.roblox.com/");
        client.DefaultRequestHeaders.Add("Origin", "https://www.roblox.com");
        long placeId = destination.PlaceId;
        string linkCode = destination.LinkCode;
        if (destination.ShareCode.Length > 0)
        {
            string payload = JsonSerializer.Serialize(new { linkId = destination.ShareCode, linkType = "Server" });
            using var response = await ResolveShareLink(client, payload,token);
            CheckPrivateResponse(response, account);
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var root = document.RootElement;
            if (!root.TryGetProperty("privateServerInviteData", out var invite) || invite.ValueKind != JsonValueKind.Object ||
                !LaunchDestination.IsPlaceId(Field(invite, "placeId"), out placeId)) throw new InvalidOperationException("Roblox did not return a valid private server. The link may have expired.");
            linkCode = Field(invite, "linkCode");
            string accessCode = Field(invite, "accessCode");
            if (Guid.TryParse(accessCode, out _)) return new(placeId, AccessCode: accessCode, LinkCode: LaunchDestination.ValidLinkCode(linkCode) ? linkCode : "");
            if (Guid.TryParse(linkCode, out _)) return new(placeId, AccessCode: linkCode);
            if (!LaunchDestination.ValidLinkCode(linkCode)) throw new InvalidOperationException("Roblox did not return a usable private server invite. Check the link and try again.");
        }
        var pageUri = new Uri($"https://www.roblox.com/games/{placeId}?privateServerLinkCode={Uri.EscapeDataString(linkCode)}");
        for (int redirect = 0; redirect < 6; redirect++)
        {
            using var response = await client.GetAsync(pageUri,token);
            if ((int)response.StatusCode is >= 300 and < 400)
            {
                var location = response.Headers.Location;
                if (location is null || !Uri.TryCreate(pageUri, location, out var next) || !LaunchDestination.IsRobloxUrl(next))
                    throw new InvalidOperationException("Roblox redirected this invite to an unsupported address.");
                if (next.AbsolutePath.StartsWith("/login", StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Roblox requires you to sign in again before resolving this invite.");
                pageUri = next; continue;
            }
            CheckPrivateResponse(response, account);
            return ParsePrivatePage(await response.Content.ReadAsStringAsync(), placeId, linkCode);
        }
        throw new InvalidOperationException("Roblox could not finish resolving this private server link. Try again.");
    }
    private static async Task<HttpResponseMessage> ResolveShareLink(HttpClient client, string payload,CancellationToken token)
    {
        const string url = "https://apis.roblox.com/sharelinks/v1/resolve-link";
        using var initialBody = new StringContent(payload, Encoding.UTF8, "application/json");
        var response = await client.PostAsync(url, initialBody,token);
        if (response.StatusCode != HttpStatusCode.Forbidden || !response.Headers.TryGetValues("x-csrf-token", out var tokens)) return response;
        client.DefaultRequestHeaders.Add("x-csrf-token", tokens.First());
        response.Dispose();
        using var retryBody = new StringContent(payload, Encoding.UTF8, "application/json");
        return await client.PostAsync(url, retryBody,token);
    }
    private static string Field(JsonElement node, string name) => node.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.String or JsonValueKind.Number ? value.ToString() : "";
    private static void CheckPrivateResponse(HttpResponseMessage response, Account account)
    {
        if (response.StatusCode == HttpStatusCode.Unauthorized) { account.Expired = true; throw new InvalidOperationException("Session expired. Sign in to this account again."); }
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException($"Roblox could not open this private server invite (HTTP {(int)response.StatusCode}). Check that the link is active and this account can join.");
    }
    internal static ResolvedLaunchDestination ParsePrivatePage(string html, long placeId, string linkCode)
    {
        // Read launch data only; never execute page scripts or navigate the user's browser.
        string decoded = WebUtility.HtmlDecode(html);
        var calls = Regex.Matches(decoded, @"(?:Roblox\.)?GameLauncher\s*\.\s*joinPrivateGame\s*\(\s*(?<place>\d+)\s*,\s*(?<quote>['""])(?<access>[a-fA-F0-9]{8}-(?:[a-fA-F0-9]{4}-){3}[a-fA-F0-9]{12})\k<quote>", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(2));
        foreach (Match call in calls)
            if (LaunchDestination.IsPlaceId(call.Groups["place"].Value, out long pagePlace) && pagePlace == placeId)
                return new(placeId, AccessCode: call.Groups["access"].Value, LinkCode: linkCode);
        var metadata = Regex.Match(decoded, @"<[^>]+\bid\s*=\s*['""]game-detail-meta-data['""][^>]*>", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase, TimeSpan.FromSeconds(2));
        if (metadata.Success)
        {
            var id = Regex.Match(metadata.Value, @"\bdata-place-id\s*=\s*['""](?<id>\d+)['""]");
            var code = Regex.Match(metadata.Value, @"\bdata-(?:private-server-)?access-code\s*=\s*['""](?<code>[a-fA-F0-9-]{36})['""]");
            if (id.Success && LaunchDestination.IsPlaceId(id.Groups["id"].Value, out long pagePlace) && pagePlace == placeId && code.Success && Guid.TryParse(code.Groups["code"].Value, out _))
                return new(placeId, AccessCode: code.Groups["code"].Value, LinkCode: linkCode);
        }
        throw new InvalidOperationException("Roblox did not grant access to this private server. Check the invite and the account's private server privacy settings.");
    }
}
