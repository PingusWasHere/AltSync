using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Security.Principal;
using System.Text.Json;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using AltSync.Core;
using Microsoft.Web.WebView2.Core;

namespace AltSync;

public partial class MainWindow : Window
{
    private readonly Vault vault;
    private readonly Library library;
    private string? libraryError;
    private TrayIcon? tray;
    private bool browserIdle;
    private readonly SemaphoreSlim windowActivity = new(1, 1);
    private readonly Queue<string> idleReplies = new();
    private readonly SessionMonitor monitor = new();
    private readonly List<ActivityEntry> activity = [];
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromSeconds(3) };
    private readonly SemaphoreSlim mutation = new(1, 1);
    private readonly bool smoke;
    private readonly bool loginSmoke;
    private readonly bool powerSmoke;
    private bool ticking, closing, scanFailed;
    private string? vaultError;
    private readonly RobloxApi api = new();
    private readonly DiscordPresence discord = new();
    private readonly GameBrowser games = new();
    private readonly ServerBrowser servers = new();
    private readonly LaunchQueue launchQueue = new();
    private Task? queueTask;
    private readonly AutoUpdater updates;
    private readonly CancellationTokenSource lifetime = new();
    private CancellationTokenSource? gameSearch;
    private bool shutdownCompleted;

    public MainWindow(bool smoke = false, bool loginSmoke = false, bool powerSmoke = false)
    {
        InitializeComponent(); this.smoke = smoke || loginSmoke || powerSmoke; this.loginSmoke = loginSmoke; this.powerSmoke = powerSmoke;
        updates = new AutoUpdater(this.smoke);
        vault = new Vault(this.smoke ? Path.Combine(Path.GetTempPath(), "AltSync-smoke") : null);
        library = new Library(vault.Root);
        try { vault.Load(); } catch (Exception ex) { vaultError = "The local vault could not be loaded. Your existing file has been preserved. " + ex.Message; }
        try { library.Load(); } catch (Exception ex) { libraryError = "Your saved library could not be loaded. Its file has been preserved. " + ex.Message; }
        Loaded += Initialize;
        StateChanged += async (_, _) => await RefreshWindowActivity();
        timer.Tick += async (_, _) => await Tick();
        Closing += async (_, e) => {
            if (shutdownCompleted) return;
            e.Cancel = true;
            if (closing) return;
            closing = true; timer.Stop(); lifetime.Cancel();
            try { if(queueTask is not null)try{await queueTask.WaitAsync(TimeSpan.FromSeconds(5));}catch{} await discord.DisposeAsync(); }
            finally { shutdownCompleted = true; _ = Dispatcher.BeginInvoke(new Action(Close)); }
        };
        Closed += (_, _) => { tray?.Dispose(); Browser.Dispose(); games.Dispose(); updates.Dispose(); };
        Log("info", "AltSync started. Account sessions are encrypted for your Windows user.");
        if (!this.smoke)
        {
            try { Process.EnterDebugMode(); }
            catch (System.ComponentModel.Win32Exception) { Log("info", "Windows did not grant debug privileges. Run the AltSync executable as administrator for handle management."); }
        }
    }
    private async void Initialize(object sender, RoutedEventArgs e)
    {
        try
        {
            var environment = await CoreWebView2Environment.CreateAsync(null, Path.Combine(vault.Root, "WebView2"));
            await Browser.EnsureCoreWebView2Async(environment);
            var core = Browser.CoreWebView2;
            core.Settings.AreHostObjectsAllowed = false; core.Settings.AreDefaultContextMenusEnabled = false;
            core.Settings.AreDevToolsEnabled = smoke; core.Settings.IsStatusBarEnabled = false;
            core.Settings.IsPasswordAutosaveEnabled = false;
            core.SetVirtualHostNameToFolderMapping("altsync.local", Path.Combine(AppContext.BaseDirectory, "Web"), CoreWebView2HostResourceAccessKind.DenyCors);
            core.NavigationStarting += (_, args) => { if (!Trusted(args.Uri)) args.Cancel = true; };
            core.NewWindowRequested += (_, args) => args.Handled = true;
            core.PermissionRequested += (_, args) => args.State = CoreWebView2PermissionState.Deny;
            core.DownloadStarting += (_, args) => args.Cancel = true;
            core.WebMessageReceived += Message;
            core.Navigate("https://altsync.local/index.html");
            if (!smoke || powerSmoke)
            {
                try { tray = new TrayIcon(RestoreWindow, Close); }
                catch (Exception ex) { Log("info", ex.Message); }
                await RefreshWindowActivity();
            }
            if (!smoke) timer.Start();
            else
            {
                await SmokeChecks.Run(core, vault.Root);
                if (powerSmoke) await CheckWindowActivity();
                if (loginSmoke)
                {
                    await core.ExecuteScriptAsync("window.loginCheckResult = 'pending'; request('addAccount').then(() => window.loginCheckResult = 'passed').catch(e => window.loginCheckResult = e.message)");
                    bool completed = false;
                    for (int i = 0; i < 240; i++)
                    {
                        await Task.Delay(250);
                        string result = await core.ExecuteScriptAsync("window.loginCheckResult");
                        if (result == "\"passed\"") { completed = true; break; }
                        if (result != "\"pending\"") throw new InvalidOperationException("Login check failed: " + result);
                    }
                    if (!completed) throw new TimeoutException("Login check did not complete.");
                }
                Close();
            }
        }
        catch (Exception ex)
        {
            if (smoke) { File.WriteAllText(Path.Combine(vault.Root, "smoke-error.txt"), ex.ToString()); Application.Current.Shutdown(1); }
            else { MessageBox.Show("AltSync could not initialize WebView2. Install the Microsoft Edge WebView2 Runtime and retry.\n\n" + ex.Message, "AltSync"); Close(); }
        }
    }
    private static bool Trusted(string url) => Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == "https" && uri.Host == "altsync.local" && uri.IsDefaultPort;
    private async void Message(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        if (!Trusted(e.Source)) return;
        string? id = null, command = null;
        try
        {
            using var document = JsonDocument.Parse(e.WebMessageAsJson);
            var message = document.RootElement;
            id = message.GetProperty("id").GetString();
            command = message.GetProperty("command").GetString()!;
            var payload = message.GetProperty("payload");
            if (command == "state") { Reply(id, State()); return; }
            if(!smoke && command is "pauseQueue" or "resumeQueue" or "cancelQueue")
            {if(command=="cancelQueue")launchQueue.Cancel();else launchQueue.Pause(command=="pauseQueue");Reply(id,State());return;}
            if(command=="searchServers")
            {
                using var search=CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);search.CancelAfter(TimeSpan.FromSeconds(20));
                Account? account=payload.TryGetProperty("accountId",out var chosen)&&chosen.TryGetInt64(out long accountId)?Account(accountId):null;
                Reply(id,await servers.Search(payload.GetProperty("placeId").GetString()??"",payload.TryGetProperty("cursor",out var cursor)?cursor.GetString()??"":"",account,search.Token));return;
            }
            if (command == "searchGames")
            {
                gameSearch?.Cancel();
                using var search = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
                search.CancelAfter(TimeSpan.FromSeconds(25)); gameSearch = search;
                try { Reply(id, await games.Search(payload.GetProperty("query").GetString() ?? "", payload.TryGetProperty("pageToken", out var page) ? page.GetString() ?? "" : "", search.Token)); }
                finally { if (ReferenceEquals(gameSearch, search)) gameSearch = null; }
                return;
            }
            if (smoke && !(loginSmoke && command == "addAccount")) throw new InvalidOperationException("Actions are disabled in smoke-test mode.");
            if (vaultError is not null && command != "openData") throw new InvalidOperationException(vaultError);
            if (!await mutation.WaitAsync(0)) throw new InvalidOperationException("Another action is running. Wait for it to finish.");
            try { Reply(id, await Execute(command, payload)); }
            finally { mutation.Release(); }
        }
        catch (Exception ex)
        {
            string error = ex switch { HttpRequestException => "Roblox could not be reached. Check your connection and try again.", TaskCanceledException => "The request timed out. Please try again.", System.Security.Cryptography.CryptographicException => "This account cannot be decrypted by this Windows user. Remove it and sign in again.", _ => ex.Message };
            if (command != "searchGames") Log("error", error);
            Reply(id, null, error);
        }
    }
    private async Task<object?> Execute(string command, JsonElement p)
    {
        switch (command)
        {
            case "addAccount":
                var login = new LoginWindow(this, vault.Root);
                Task? loginCheck = loginSmoke ? LoginSmokeChecks.Run(login, vault.Root) : null;
                var added = await login.ShowForAccountAsync();
                if (loginCheck is not null) await loginCheck;
                if (added is not null) SaveAccount(added);
                break;
            case "importCookie":
                var imported = await new CookieImportWindow(this).ShowForAccountAsync();
                if (imported is not null) SaveAccount(imported);
                break;
            case "removeAccount":
                if(launchQueue.Status.Active)throw new InvalidOperationException("Wait for the launch queue to finish before removing an account.");
                var removed = Account(p.GetProperty("id").GetInt64());
                vault.Data.Accounts.Remove(removed); vault.Save(); Log("info", $"@{removed.Username} removed from AltSync."); break;
            case "editAccount":
                var edited = Account(p.GetProperty("id").GetInt64());
                var group = p.GetProperty("group").GetString()?.Trim() ?? "";
                if (group.Length > 40) throw new ArgumentException("Group names must be 40 characters or fewer.");
                edited.Group = group.Length == 0 ? "Personal" : group; vault.Save(); break;
            case "validateAccount":
                var account = Account(p.GetProperty("id").GetInt64());
                try { var valid = await api.Validate(Vault.Unprotect(account.Secret)); account.Username = valid.Username; account.DisplayName = valid.DisplayName; account.Avatar = valid.Avatar; account.Expired = false; }
                catch (InvalidOperationException) { account.Expired = true; vault.Save(); throw; }
                vault.Save(); Log("success", $"@{account.Username} session verified."); break;
            case "saveSettings":
                var prefs = p.Deserialize<Preferences>(Vault.Json) ?? throw new ArgumentException("Invalid settings.");
                if (prefs.LaunchDelay < 3 || prefs.LaunchDelay > 30) throw new ArgumentException("Launch delay must be between 3 and 30 seconds.");
                PerformanceProfiles.Flags(prefs.Performance);
                if (prefs.PlaceId.Length > LaunchDestination.MaxLength || prefs.JobId.Length > 100) throw new ArgumentException("Invalid launch target.");
                vault.Data.Settings = prefs; vault.Save(); scanFailed = false; await RefreshWindowActivity(); Log("info", "Preferences saved."); break;
            case "saveLaunchProfile":
                RequireLibrary();
                long[] profileIds = p.GetProperty("ids").EnumerateArray().Select(n => n.GetInt64()).Distinct().ToArray();
                foreach (long accountId in profileIds) _ = Account(accountId);
                library.SaveProfile(p.GetProperty("name").GetString() ?? "", profileIds, p.GetProperty("placeId").GetString() ?? "", p.GetProperty("jobId").GetString() ?? "", vault.Data.Settings);
                break;
            case "removeLaunchProfile": RequireLibrary(); library.RemoveProfile(p.GetProperty("id").GetString() ?? ""); break;
            case "useLaunchProfile":
                RequireLibrary(); var savedProfile = library.Profile(p.GetProperty("id").GetString() ?? "");
                var availableIds = savedProfile.AccountIds.Where(id => vault.Data.Accounts.Any(a => a.Id == id)).ToArray();
                if (availableIds.Length == 0) throw new InvalidOperationException("None of this profile's accounts are currently saved. Add an account or create a new profile.");
                vault.Data.Settings.PlaceId = savedProfile.PlaceId; vault.Data.Settings.JobId = savedProfile.JobId; vault.Data.Settings.Performance = savedProfile.Performance;
                vault.Data.Settings.AutoApply = savedProfile.AutoApply; vault.Data.Settings.LaunchDelay = savedProfile.LaunchDelay; vault.Save();
                return new { state = State(), launchProfile = savedProfile with { AccountIds = availableIds } };
            case "favoriteGame":
                RequireLibrary(); string favoritePlace = p.GetProperty("placeId").GetString() ?? "";
                var favoriteGame = library.Data.Games.Find(g => g.Game.PlaceId == favoritePlace)?.Game ?? games.Cached(favoritePlace) ?? await games.Details(favoritePlace, lifetime.Token);
                library.Favorite(favoriteGame, p.GetProperty("favorite").GetBoolean()); break;
            case "clearRecentGames": RequireLibrary(); library.ClearRecent(); break;
            case "refreshLibrary":
            {
                RequireLibrary();
                using var refresh = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
                refresh.CancelAfter(TimeSpan.FromSeconds(12));
                foreach (var saved in library.Data.Games.Where(g => DateTimeOffset.UtcNow - g.UpdatedAt > TimeSpan.FromMinutes(15)).OrderBy(g => g.UpdatedAt).Take(6).ToArray())
                {
                    try { library.Refresh(await games.Details(saved.Game.PlaceId, refresh.Token)); }
                    catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or JsonException or InvalidOperationException) { if (refresh.IsCancellationRequested) break; }
                }
                break;
            }
            case "launch":
                StartBatch(LaunchRequests(p));break;
            case "retryQueue": StartBatch(launchQueue.Failed());break;
            case "focusSession": WindowLayouts.Focus(monitor,p.GetProperty("pid").GetInt32());break;
            case "arrangeWindows":
                RequireLibrary();var placements=WindowLayouts.Arrange(monitor,p.GetProperty("columns").GetInt32(),new System.Windows.Interop.WindowInteropHelper(this).Handle);library.SaveWindows(placements);Log("info",$"Arranged {placements.Length} Roblox windows.");break;
            case "saveWindowPositions": RequireLibrary();library.SaveWindows(WindowLayouts.Capture(monitor));Log("info","Window positions saved.");break;
            case "copyDiagnostics":
                var resources=SystemResources.Sample();var installed=RobloxInstall.Find();
                string report=$"AltSync 0.2.0\nWindows: {Environment.OSVersion.Version}\n.NET: {Environment.Version}\nWebView2: {Browser.CoreWebView2.Environment.BrowserVersionString}\nRoblox build: {(installed is null?"Not installed":FileVersionInfo.GetVersionInfo(installed).FileVersion??"Unknown")}\nCPU threads: {Environment.ProcessorCount}\nAvailable memory: {resources.AvailableMb} MB\nMulti-instance: {(scanFailed?"Paused":"Ready")}\nFailed launches: {launchQueue.Status.Entries.Count(e=>e.Status=="failed")}\nAccount data, cookies and private links are excluded.";
                Clipboard.SetText(report);break;
            case "exportBackup":
                RequireLibrary();if(launchQueue.Status.Active)throw new InvalidOperationException("Pause your work until the launch queue finishes before creating a backup.");
                var exportIds=p.GetProperty("ids").EnumerateArray().Select(x=>x.GetInt64()).ToHashSet();
                var saveDialog=new Microsoft.Win32.SaveFileDialog{Filter="AltSync encrypted backup|*.altsync",DefaultExt=".altsync",FileName="AltSync-backup.altsync",OverwritePrompt=true};
                if(saveDialog.ShowDialog(this)==true){var backup=new BackupWindow(this,vault,library,exportIds);if(backup.ShowDialog()==true&&backup.Exported is not null){File.WriteAllBytes(saveDialog.FileName,backup.Exported);Log("success","Encrypted backup saved.");}}break;
            case "importBackup":
                RequireLibrary();if(launchQueue.Status.Active)throw new InvalidOperationException("Wait for the launch queue to finish before importing a backup.");
                var openDialog=new Microsoft.Win32.OpenFileDialog{Filter="AltSync encrypted backup|*.altsync",CheckFileExists=true};
                if(openDialog.ShowDialog(this)==true){if(new FileInfo(openDialog.FileName).Length>32*1024*1024)throw new InvalidDataException("This backup is too large.");var restore=new BackupWindow(this,vault,library,new HashSet<long>(),File.ReadAllBytes(openDialog.FileName));if(restore.ShowDialog()==true&&restore.Imported is not null){PortableBackup.Import(vault,library,restore.Imported);Log("success","Encrypted backup imported.");}}break;
            case "releaseSingleton":
                int count = await Task.Run(MultiInstance.Release); Log("success", $"Released {count} Roblox singleton event handle(s)."); break;
            case "applyProfile":
                string profile = p.GetProperty("profile").GetString()!;
                string text = new PerformanceProfiles(vault.Root).Apply(RobloxInstall.Find() ?? throw new InvalidOperationException("Roblox Player was not found."), profile);
                vault.Data.Settings.Performance = profile; vault.Save(); Log("success", text); break;
            case "stopSession":
                monitor.Stop(p.GetProperty("pid").GetInt32()); Log("info", "Roblox session stopped."); break;
            case "clearActivity": activity.Clear(); break;
            case "checkUpdates": _ = updates.Check(vault.Data.Settings.AutomaticUpdates, lifetime.Token); break;
            case "openData": Process.Start(new ProcessStartInfo("explorer.exe", vault.Root) { UseShellExecute = true }); break;
            default: throw new ArgumentException("Unknown AltSync command.");
        }
        return State();
    }
    private LaunchRequest[] LaunchRequests(JsonElement p)
    {
        var ids=p.GetProperty("ids").EnumerateArray().Select(n=>n.GetInt64()).Distinct().ToArray();if(ids.Length is <1 or >100)throw new ArgumentException("Select between 1 and 100 accounts.");
        string value=p.GetProperty("placeId").GetString()?.Trim()??"",job=p.GetProperty("jobId").GetString()?.Trim()??"";
        bool last=p.TryGetProperty("last",out var useLast)&&useLast.GetBoolean();
        var target=value.Length==0?null:LaunchDestination.Parse(value);
        if(target is null&&job.Length>0)throw new ArgumentException("A server instance ID also needs a game ID or game link.");
        if(job.Length>0&&!Guid.TryParse(job,out _))throw new ArgumentException("Use a valid server UUID, or leave it empty.");
        var result=new List<LaunchRequest>();
        foreach(var id in ids){var account=Account(id);var recent=last?library.Last(id)??throw new InvalidOperationException($"No previous game is saved for @{account.Username}."):null;result.Add(new(id,account.DisplayName,recent?.Value??target?.Value??"",recent?.JobId??(target?.IsPrivate==true?"":job)));}
        if(!last){vault.Data.Settings.PlaceId=target?.Value??"";vault.Data.Settings.JobId=target?.IsPrivate==true?"":job;vault.Save();}return result.ToArray();
    }
    private void StartBatch(IReadOnlyList<LaunchRequest> requests)
    {
        if(launchQueue.Status.Active)throw new InvalidOperationException("A launch queue is already running.");
        if(requests.Count==0)throw new InvalidOperationException("There are no failed launches to retry.");
        if(!vault.Data.Settings.MultiInstance&&(requests.Count>1||monitor.Snapshot().Count>0))throw new InvalidOperationException("Enable multi-instance before opening several Roblox sessions.");
        var executable=RobloxInstall.Find()??throw new InvalidOperationException("Roblox Player was not found. Install it and launch it once, then retry.");
        var prefs=JsonSerializer.Deserialize<Preferences>(JsonSerializer.Serialize(vault.Data.Settings,Vault.Json),Vault.Json)!;
        queueTask=RunBatch(requests,executable,prefs);
    }
    private async Task RunBatch(IReadOnlyList<LaunchRequest> requests,string executable,Preferences prefs)
    {
        try
        {
            bool applied=false;
            await launchQueue.Run(requests,async request=>
            {
                await mutation.WaitAsync(lifetime.Token);
                try
                {
                    if(!applied&&prefs.AutoApply){Log("info",new PerformanceProfiles(vault.Root).Apply(executable,prefs.Performance));applied=true;}
                    var account=Account(request.AccountId);var destination=request.PlaceId.Length==0?null:LaunchDestination.Parse(request.PlaceId);
                    long place=await Launch(account,executable,destination,request.JobId,prefs);
                    if(place>0&&libraryError is null)
                    {
                        string id=place.ToString(System.Globalization.CultureInfo.InvariantCulture);
                        var game=games.Cached(id)??library.Data.Games.Find(g=>g.Game.PlaceId==id)?.Game??new BrowseGame("0",id,$"Place {id}","",0,"");
                        try{library.Played(game);library.Remember(account.Id,request.PlaceId,destination?.IsPrivate==true?"":request.JobId,game.Name);}catch{Log("info","Roblox started, but its recent destination could not be saved.");}
                    }
                    try{vault.Save();}catch{Log("info","Roblox started, but local account changes could not be saved.");}
                }
                finally{mutation.Release();}
            },prefs.LaunchDelay,prefs.AdaptiveLaunchDelay,lifetime.Token);
        }
        catch(Exception ex){if(!closing)Log("error",ex is HttpRequestException?"Roblox could not be reached.":ex.Message);}
    }
    private async Task<long> Launch(Account account, string executable, LaunchDestination? destination, string job,Preferences prefs)
    {
        if (account.Expired) throw new InvalidOperationException("Session expired. Sign in again before launching.");
        var resolved = destination is null?null:await api.ResolveDestination(account, destination, job,lifetime.Token);
        if (prefs.MultiInstance) { int n = await Task.Run(MultiInstance.Release); if (n > 0) Log("info", $"Released {n} singleton event handle(s) before launch."); }
        string ticket = await api.Ticket(account,lifetime.Token);
        string uri = RobloxLaunch.Uri(ticket,resolved,DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(executable)! };
        start.ArgumentList.Add(uri);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Windows could not start Roblox.");
        await Task.Delay(1500,lifetime.Token);
        if (process.HasExited) throw new InvalidOperationException("Roblox exited during startup. Check the installed client and try again.");
        monitor.Track(process, account);
        if(prefs.RestoreWindowPositions&&library.Data.Windows.Find(w=>w.AccountId==account.Id) is {} position)_=WindowLayouts.Restore(monitor,process.Id,position,lifetime.Token).ContinueWith(_=>{},TaskScheduler.Default);
        Log("success", $"Launch started for @{account.Username} · {(resolved is null?"Roblox home":$"place {resolved.PlaceId}{(resolved.IsPrivate?" · private server":"")}")} · PID {process.Id}.");
        return resolved?.PlaceId??0;
    }
    private async Task Tick()
    {
        if (ticking || closing) return; ticking = true;
        try
        {
            if (vaultError is null) updates.Pulse(vault.Data.Settings.AutomaticUpdates, lifetime.Token);
            if (vaultError is null && vault.Data.Settings.MultiInstance && !scanFailed && mutation.CurrentCount > 0)
            {
                try { int count = await Task.Run(MultiInstance.Release); if (count > 0) Log("info", $"Automatically released {count} Roblox singleton event handle(s)."); }
                catch (Exception ex) { scanFailed = true; Log("error", "Automatic multi-instance paused: " + ex.Message); }
            }
            if (!closing && Browser.CoreWebView2 is not null && !browserIdle) Browser.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(new { type = "state", data = State() }, Vault.Json));
            else if (!closing) { _ = monitor.Snapshot(); if (vaultError is null) discord.Update(vault.Data.Settings.DiscordEnabled); }
        }
        finally { ticking = false; }
    }
    private Account Account(long id) => vault.Data.Accounts.FirstOrDefault(a => a.Id == id) ?? throw new ArgumentException("Account not found.");
    private void RequireLibrary() { if (libraryError is not null) throw new InvalidOperationException(libraryError); }
    private void SaveAccount(Account account)
    {
        vault.AddOrRefresh(account);
        Log("success", $"@{account.Username} added to your account vault.");
    }
    private object State()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var sessions = monitor.Snapshot();
        if (!smoke && !closing && vaultError is null) discord.Update(vault.Data.Settings.DiscordEnabled);
        return new {
            accounts = vault.Data.Accounts.Select(a => new { a.Id, a.Username, a.DisplayName, a.Group, a.Avatar, a.Expired, a.AddedAt }),
            settings = vault.Data.Settings, sessions, activity = activity.AsEnumerable().Reverse().ToArray(),
            library = new { profiles = library.Data.Profiles, games = library.Data.Games, last = library.Data.Destinations.Select(d=>new{d.AccountId,d.Name,d.Private}),error = libraryError },queue=launchQueue.Status,
            system = new { administrator = new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator), robloxInstalled = RobloxInstall.Find() is not null, multiInstanceError = scanFailed, vaultError, version = "0.2.0", smoke, updates = updates.Status, discord = smoke ? new DiscordPresenceStatus("preview", "Preview mode") : discord.Status,
                discordBranding = new { DiscordBranding.Profile.Name, DiscordBranding.Profile.Details, DiscordBranding.Profile.State, buttons = DiscordBranding.Profile.Buttons.Select(button => button.Label) } }
        };
    }
    private void Log(string kind, string message) { activity.Add(new(DateTimeOffset.Now, kind, message)); if (activity.Count > 250) activity.RemoveAt(0); }
    private void Reply(string? id, object? data, string? error = null)
    {
        if (closing || Browser.CoreWebView2 is null) return;
        var json = JsonSerializer.Serialize(new { id, data, error }, Vault.Json);
        if (browserIdle) { if (idleReplies.Count < 256) idleReplies.Enqueue(json); }
        else Browser.CoreWebView2.PostWebMessageAsJson(json);
    }
    private void RestoreWindow()
    {
        Show(); ShowInTaskbar = true; WindowState = WindowState.Normal; Activate();
        _ = RefreshWindowActivity();
    }
    private async Task RefreshWindowActivity()
    {
        if (smoke && !powerSmoke || closing || Browser.CoreWebView2 is null) return;
        await windowActivity.WaitAsync();
        try
        {
            if (closing) return;
            bool minimized = WindowState == WindowState.Minimized;
            browserIdle = minimized;
            if (minimized)
            {
                Browser.Visibility = Visibility.Hidden;
                if (vault.Data.Settings.MinimizeToTray && tray is not null) { Hide(); ShowInTaskbar = false; }
                await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render);
                if (!closing && vault.Data.Settings.SleepWhenMinimized) await Browser.CoreWebView2.TrySuspendAsync();
            }
            else
            {
                Browser.CoreWebView2.Resume(); Browser.Visibility = Visibility.Visible;
                while (idleReplies.TryDequeue(out var reply)) Browser.CoreWebView2.PostWebMessageAsJson(reply);
                Browser.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(new { type = "state", data = State() }, Vault.Json));
            }
        }
        catch (Exception ex) { if (!closing) Log("info", "Window activity: " + ex.Message); }
        finally { windowActivity.Release(); }
    }
    private async Task CheckWindowActivity()
    {
        if (tray is null) throw new InvalidOperationException("Native notification icon was not created.");
        vault.Data.Settings.SleepWhenMinimized = true; vault.Data.Settings.MinimizeToTray = true;
        WindowState = WindowState.Minimized; await RefreshWindowActivity();
        for (int i = 0; i < 30 && !Browser.CoreWebView2.IsSuspended; i++) await Task.Delay(100);
        bool suspended = Browser.CoreWebView2.IsSuspended && browserIdle && !IsVisible && !ShowInTaskbar;
        RestoreWindow(); await RefreshWindowActivity();
        bool resumed = !Browser.CoreWebView2.IsSuspended && !browserIdle && IsVisible && ShowInTaskbar && Browser.Visibility == Visibility.Visible;
        File.WriteAllText(Path.Combine(vault.Root, "power-results.json"), JsonSerializer.Serialize(new { trayCreated = tray is not null, suspended, resumed }, Vault.Json));
        if (!suspended || !resumed) throw new InvalidOperationException("The hidden WebView2 did not suspend and resume correctly.");
    }
    private void DragWindow(object sender, MouseButtonEventArgs e) { if (e.ClickCount == 2) Maximize(sender, e); else if (e.LeftButton == MouseButtonState.Pressed) DragMove(); }
    private void Minimize(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void Maximize(object sender, RoutedEventArgs e) => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    private void CloseWindow(object sender, RoutedEventArgs e) => Close();
}
