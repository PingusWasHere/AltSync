using System.Net;
using System.Net.Http;
using System.Text.Json;

namespace AltSync.Core;
public sealed record PublicServer(string Id,int Playing,int Capacity,int? Ping);
public sealed record ServerPage(IReadOnlyList<PublicServer> Servers,string NextPageCursor);
public sealed class ServerBrowser
{
    private readonly Func<Account?,HttpClient> factory;
    private readonly Dictionary<string,(DateTimeOffset Time,ServerPage Page)> cache=[];
    public ServerBrowser(Func<Account?,HttpClient>? factory=null)=>this.factory=factory??Client;
    private static HttpClient Client(Account? account)
    {
        var handler=new HttpClientHandler{AllowAutoRedirect=false,UseCookies=account is not null};
        if(account is not null){var jar=new CookieContainer();jar.Add(new Cookie(".ROBLOSECURITY",Vault.Unprotect(account.Secret),"/",".roblox.com"){Secure=true,HttpOnly=true});handler.CookieContainer=jar;}
        return new HttpClient(handler){Timeout=TimeSpan.FromSeconds(15)};
    }
    public async Task<ServerPage> Search(string placeId,string cursor="",Account? account=null,CancellationToken token=default)
    {
        if(!LaunchDestination.IsPlaceId(placeId,out _)||cursor.Length>2048||cursor.Any(char.IsControl))throw new ArgumentException("Choose a valid game first.");
        var key=$"{account?.Id??0}:{placeId}:{cursor}";
        if(cache.TryGetValue(key,out var saved)&&DateTimeOffset.UtcNow-saved.Time<TimeSpan.FromSeconds(30))return saved.Page;
        using var client=factory(account);
        using var response=await client.GetAsync($"https://games.roblox.com/v1/games/{placeId}/servers/Public?sortOrder=Asc&excludeFullGames=true&limit=100&cursor={Uri.EscapeDataString(cursor)}",HttpCompletionOption.ResponseHeadersRead,token);
        if(response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)throw new InvalidOperationException("Roblox requires a valid selected account to list servers. Verify its session and retry.");
        if(response.StatusCode==HttpStatusCode.TooManyRequests)throw new InvalidOperationException("Roblox is receiving too many requests. Wait a moment before refreshing.");
        response.EnsureSuccessStatusCode();
        await using var input=await response.Content.ReadAsStreamAsync(token);using var buffer=new System.IO.MemoryStream();byte[] bytes=new byte[16384];int read;
        while((read=await input.ReadAsync(bytes,token))>0){if(buffer.Length+read>2*1024*1024)throw new InvalidOperationException("Roblox returned an oversized server list.");buffer.Write(bytes,0,read);}
        using var json=JsonDocument.Parse(buffer.ToArray());
        if(!json.RootElement.TryGetProperty("data",out var items)||items.ValueKind!=JsonValueKind.Array)throw new InvalidOperationException("Roblox returned an invalid server list.");
        var servers=new List<PublicServer>();var seen=new HashSet<string>();
        foreach(var item in items.EnumerateArray())
        {
            if(!item.TryGetProperty("id",out var id)||!Guid.TryParse(id.GetString(),out var uuid)||!item.TryGetProperty("playing",out var playing)||!playing.TryGetInt32(out int count)||!item.TryGetProperty("maxPlayers",out var maximum)||!maximum.TryGetInt32(out int capacity)||count<0||capacity is <1 or >2000||count>=capacity||!seen.Add(uuid.ToString()))continue;
            int? ping=item.TryGetProperty("ping",out var latency)&&latency.TryGetInt32(out int p)&&p is >=0 and <=60000?p:null;
            servers.Add(new(uuid.ToString(),count,capacity,ping));if(servers.Count==100)break;
        }
        string next=json.RootElement.TryGetProperty("nextPageCursor",out var nextItem)&&nextItem.ValueKind==JsonValueKind.String?nextItem.GetString()??"":"";
        if(next.Length>2048||next.Any(char.IsControl)||next==cursor)next="";
        var page=new ServerPage(servers,next);if(cache.Count>=12)cache.Remove(cache.MinBy(p=>p.Value.Time).Key);cache[key]=(DateTimeOffset.UtcNow,page);return page;
    }
}
