using System.IO;
using System.Text.Json;

namespace AltSync.Core;

public sealed record LaunchProfile(string Id, string Name, long[] AccountIds, string PlaceId, string JobId, string Performance, bool AutoApply, int LaunchDelay);
public sealed record SavedGame(BrowseGame Game, bool Favorite, DateTimeOffset? LastPlayed, DateTimeOffset UpdatedAt);
public sealed record AccountDestination(long AccountId,string Value,string JobId,string Name,bool Private);
public sealed record WindowPlacement(long AccountId,int X,int Y,int Width,int Height);
public sealed class LibraryData
{
    public List<LaunchProfile> Profiles { get; set; } = [];
    public List<SavedGame> Games { get; set; } = [];
    public List<AccountDestination> Destinations { get; set; } = [];
    public List<WindowPlacement> Windows { get; set; } = [];
}

// Separate from the credential vault: this file contains launch choices and public game metadata only.
public sealed class Library
{
    private readonly string file;
    public LibraryData Data { get; private set; } = new();
    public Library(string root) { file = Path.Combine(root, "library.json"); }
    public void Load()
    {
        if (!File.Exists(file)) return;
        var loaded = JsonSerializer.Deserialize<LibraryData>(File.ReadAllText(file), Vault.Json);
        ValidateData(loaded);
        Data = loaded!;
    }
    public static void ValidateData(LibraryData? loaded)
    {
        if(loaded?.Profiles is null||loaded.Games is null||loaded.Destinations is null||loaded.Windows is null||loaded.Profiles.Count>30||loaded.Games.Count>150||loaded.Destinations.Count>1000||loaded.Windows.Count>1000)throw new InvalidDataException("The saved library is invalid. Its file has been preserved.");
        foreach(var p in loaded.Profiles)ValidateProfile(p);foreach(var g in loaded.Games)ValidateGame(g.Game);
        foreach(var d in loaded.Destinations){if(d.AccountId<=0||d.Name.Length>200||d.JobId.Length>100)throw new InvalidDataException("Invalid recent destination.");var target=LaunchDestination.Parse(d.Value);if(d.JobId.Length>0&&(!Guid.TryParse(d.JobId,out _)||target.IsPrivate))throw new InvalidDataException("Invalid saved server.");}
        foreach(var w in loaded.Windows)if(w.AccountId<=0||w.Width is <320 or >20000||w.Height is <240 or >20000||Math.Abs((long)w.X)>100000||Math.Abs((long)w.Y)>100000)throw new InvalidDataException("Invalid saved window position.");
        if(loaded.Profiles.Select(p=>p.Id).Distinct().Count()!=loaded.Profiles.Count||loaded.Games.Select(g=>g.Game.PlaceId).Distinct().Count()!=loaded.Games.Count||loaded.Destinations.Select(d=>d.AccountId).Distinct().Count()!=loaded.Destinations.Count||loaded.Windows.Select(w=>w.AccountId).Distinct().Count()!=loaded.Windows.Count)throw new InvalidDataException("Duplicate library entries.");
    }
    private void Commit(Action<LibraryData> change)
    {
        var next = new LibraryData { Profiles = [.. Data.Profiles], Games = [.. Data.Games],Destinations=[..Data.Destinations],Windows=[..Data.Windows] };
        change(next);
        var keepRecent = next.Games.Where(g => g.LastPlayed is not null).OrderByDescending(g => g.LastPlayed).Take(30).Select(g => g.Game.PlaceId).ToHashSet();
        next.Games = next.Games.Where(g => g.Favorite || keepRecent.Contains(g.Game.PlaceId)).ToList();
        File.WriteAllText(file + ".tmp", JsonSerializer.Serialize(next, Vault.Json)); File.Move(file + ".tmp", file, true); Data = next;
    }
    public LaunchProfile SaveProfile(string name, IEnumerable<long> ids, string placeId, string jobId, Preferences settings)
    {
        var p = new LaunchProfile(Guid.NewGuid().ToString("N"), name.Trim(), ids.Distinct().ToArray(), LaunchDestination.Parse(placeId).Value, jobId.Trim(), settings.Performance, settings.AutoApply, settings.LaunchDelay);
        ValidateProfile(p);
        if (Data.Profiles.Count >= 30) throw new InvalidOperationException("You can save up to 30 launch profiles.");
        if (Data.Profiles.Any(old => old.Name.Equals(p.Name, StringComparison.OrdinalIgnoreCase))) throw new ArgumentException("A profile already uses this name.");
        Commit(next => next.Profiles.Add(p)); return p;
    }
    public LaunchProfile Profile(string id) => Data.Profiles.Find(p => p.Id == id) ?? throw new ArgumentException("This launch profile no longer exists.");
    public void RemoveProfile(string id) { _ = Profile(id); Commit(next => next.Profiles.RemoveAll(p => p.Id == id)); }
    public void Favorite(BrowseGame game, bool favorite)
    {
        ValidateGame(game);
        if (favorite && !Data.Games.Any(g => g.Game.PlaceId == game.PlaceId && g.Favorite) && Data.Games.Count(g => g.Favorite) >= 100) throw new InvalidOperationException("You can save up to 100 favorite games.");
        Upsert(game, old => new(game, favorite, old?.LastPlayed, DateTimeOffset.UtcNow));
    }
    public void Played(BrowseGame game) { ValidateGame(game); Upsert(game, old => new(game, old?.Favorite ?? false, DateTimeOffset.UtcNow, old?.UpdatedAt ?? (game.UniverseId=="0"?DateTimeOffset.MinValue:DateTimeOffset.UtcNow))); }
    public void Refresh(BrowseGame game) { ValidateGame(game); if (Data.Games.Any(g => g.Game.PlaceId == game.PlaceId)) Upsert(game, old => old! with { Game = game, UpdatedAt = DateTimeOffset.UtcNow }); }
    private void Upsert(BrowseGame game, Func<SavedGame?, SavedGame> change) => Commit(next => { int i = next.Games.FindIndex(g => g.Game.PlaceId == game.PlaceId); var value = change(i < 0 ? null : next.Games[i]); if (i < 0) next.Games.Add(value); else next.Games[i] = value; });
    public void ClearRecent() => Commit(next => { next.Games = next.Games.Select(g => g with { LastPlayed = null }).ToList(); });
    public AccountDestination? Last(long accountId)=>Data.Destinations.Find(d=>d.AccountId==accountId);
    public void Remember(long accountId,string value,string job,string name)=>Commit(next=>{next.Destinations.RemoveAll(d=>d.AccountId==accountId);next.Destinations.Add(new(accountId,value,job,name,LaunchDestination.Parse(value).IsPrivate));next.Destinations=next.Destinations.TakeLast(1000).ToList();});
    public void SaveWindows(IEnumerable<WindowPlacement> positions)=>Commit(next=>{foreach(var w in positions){next.Windows.RemoveAll(p=>p.AccountId==w.AccountId);next.Windows.Add(w);}next.Windows=next.Windows.TakeLast(1000).ToList();});
    private static void ValidateProfile(LaunchProfile p)
    {
        if (p is null || !Guid.TryParseExact(p.Id, "N", out _) || string.IsNullOrWhiteSpace(p.Name) || p.Name.Length > 50 || p.Name.Any(char.IsControl) || p.AccountIds is null || p.AccountIds.Length is < 1 or > 100 || p.AccountIds.Any(id => id <= 0) || p.AccountIds.Distinct().Count() != p.AccountIds.Length || p.LaunchDelay is < 3 or > 30) throw new ArgumentException("Choose a profile name and between 1 and 100 accounts.");
        var destination = LaunchDestination.Parse(p.PlaceId);
        if (p.JobId is null || p.JobId.Length > 100 || p.JobId.Length > 0 && !Guid.TryParse(p.JobId, out _) || destination.IsPrivate && p.JobId.Length > 0) throw new ArgumentException("Use a valid server UUID, or leave it empty.");
        PerformanceProfiles.Flags(p.Performance);
    }
    private static void ValidateGame(BrowseGame g)
    {
        if (g is null || !long.TryParse(g.PlaceId, out long place) || place <= 0 || !long.TryParse(g.UniverseId, out long universe) || universe < 0 || string.IsNullOrWhiteSpace(g.Name) || g.Name.Length > 200 || g.Creator is null || g.Creator.Length > 200 || g.Players < 0 || g.Icon is null || g.Icon.Length > 0 && !GameBrowser.ImageUrl(g.Icon, out _)) throw new ArgumentException("Invalid saved game.");
    }
}
