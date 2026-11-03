using System.ComponentModel;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using AltSync.Core;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace AltSync;

public sealed class LoginWindow : Window
{
    private const string LoginUrl = "https://www.roblox.com/login";
    private readonly string root;
    private readonly RobloxApi api;
    private readonly Action<CoreWebView2>? configureBrowser;
    private readonly Grid content = new();
    private WebView2 browser = CreateBrowser();
    private readonly Button retry = new() { Content = "Retry", Padding = new Thickness(12, 9, 12, 9), Margin = new Thickness(0, 0, 8, 0), IsEnabled = false };
    private readonly TextBlock status = new() { Text = "Starting Chromium…", Foreground = Brushes.LightGray, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 18, 0), FontSize = 12 };
    private readonly TaskCompletionSource<Account?> result = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<CoreWebView2> ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly CancellationTokenSource lifetime = new();
    private CancellationTokenSource? detection;
    private Task? detectionTask;
    private bool started, initializing, allowClose, closePending, accepted, verifying;
    private Account? account;
    internal Task<CoreWebView2> BrowserReady => ready.Task;
    internal bool NavigationSucceeded { get; private set; }
    internal int HttpStatus { get; private set; }
    internal string? LastError { get; private set; }

    public LoginWindow(Window owner, string root) : this(owner, root, new RobloxApi()) { }

    internal LoginWindow(Window owner, string root, RobloxApi api, Action<CoreWebView2>? configureBrowser = null)
    {
        this.root = root; this.api = api; this.configureBrowser = configureBrowser;
        Owner = owner; Icon = owner.Icon; Title = "AltSync · Sign in to Roblox"; Width = 1040; Height = 780;
        MinWidth = 760; MinHeight = 600; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = new SolidColorBrush(Color.FromRgb(15, 15, 15));
        content.RowDefinitions.Add(new RowDefinition());
        content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        content.Children.Add(browser);
        var bar = new Grid { Margin = new Thickness(16) };
        bar.ColumnDefinitions.Add(new ColumnDefinition());
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(retry, 1); Grid.SetRow(bar, 1);
        bar.Children.Add(status); bar.Children.Add(retry);
        content.Children.Add(bar); Content = content;
        retry.Click += async (_, _) => await InitializeBrowser(true);
        Loaded += async (_, _) => { if (started) return; started = true; await InitializeBrowser(false); };
        Closing += ClosingWindow;
        Closed += (_, _) =>
        {
            lifetime.Cancel(); browser.Dispose();
            if (Owner is { IsVisible: true } ownerWindow) { ownerWindow.IsEnabled = true; ownerWindow.Activate(); }
            ready.TrySetCanceled();
            result.TrySetResult(accepted ? account : null);
        };
    }

    // ShowDialog would run a nested message loop inside WebMessageReceived and can
    // deadlock the second WebView2. Await Closed instead, leaving Chromium callbacks free.
    public async Task<Account?> ShowForAccountAsync()
    {
        if (IsVisible) throw new InvalidOperationException("The sign-in window is already open.");
        Owner.IsEnabled = false;
        try { Show(); return await result.Task; }
        finally { if (Owner.IsVisible) Owner.IsEnabled = true; }
    }

    private static WebView2 CreateBrowser() => new() { DefaultBackgroundColor = System.Drawing.Color.FromArgb(15, 15, 15) };

    private async Task InitializeBrowser(bool recreate)
    {
        if (initializing || closePending || allowClose) return;
        initializing = true; retry.IsEnabled = false;
        NavigationSucceeded = false; LastError = null; status.Text = "Starting Chromium…";
        try
        {
            if (recreate)
            {
                await StopDetection();
                await ClearProfile();
                if (closePending) return;
                content.Children.Remove(browser); browser.Dispose();
                browser = CreateBrowser(); content.Children.Insert(0, browser);
            }
            var path = Path.Combine(root, "SignIn", Guid.NewGuid().ToString("N"));
            var environment = await CoreWebView2Environment.CreateAsync(null, path).WaitAsync(TimeSpan.FromSeconds(20), lifetime.Token);
            await browser.EnsureCoreWebView2Async(environment).WaitAsync(TimeSpan.FromSeconds(20), lifetime.Token);
            lifetime.Token.ThrowIfCancellationRequested();
            var core = browser.CoreWebView2;
            core.Settings.IsWebMessageEnabled = false;
            core.Settings.AreHostObjectsAllowed = false;
            core.Settings.IsPasswordAutosaveEnabled = false;
            core.Settings.IsGeneralAutofillEnabled = false;
            core.PermissionRequested += (_, e) => e.State = CoreWebView2PermissionState.Deny;
            core.DownloadStarting += (_, e) => e.Cancel = true;
            core.NewWindowRequested += (_, e) => { e.Handled = true; if (Allowed(e.Uri)) core.Navigate(e.Uri); };
            core.NavigationStarting += (_, e) =>
            {
                if (!Allowed(e.Uri)) { e.Cancel = true; status.Text = "This sign-in window only opens Roblox pages."; return; }
                if (!verifying) status.Text = "Loading roblox.com…";
            };
            core.DOMContentLoaded += (_, _) =>
            {
                if (closePending || !Allowed(core.Source)) return;
                if (!verifying) status.Text = "Sign in to Roblox. Your account will be added automatically.";
            };
            core.NavigationCompleted += (_, e) =>
            {
                if (closePending) return;
                HttpStatus = e.HttpStatusCode;
                NavigationSucceeded = e.IsSuccess && e.HttpStatusCode < 400 && Allowed(core.Source);
                retry.IsEnabled = !verifying;
                if (!NavigationSucceeded)
                {
                    if (!verifying) Error(e.IsSuccess ? $"Roblox returned HTTP {e.HttpStatusCode}. Select Retry." : $"Could not load Roblox ({e.WebErrorStatus}). Check your connection and select Retry.");
                }
            };
            core.ProcessFailed += (_, e) =>
            {
                if (closePending) return;
                detection?.Cancel(); retry.IsEnabled = true;
                Error($"The Chromium browser stopped responding ({e.ProcessFailedKind}). Select Retry.");
            };
            // Use Chromium's supported DevTools API through WebView2. No remote
            // debugging port, credential logging, or browser security overrides.
            await core.CallDevToolsProtocolMethodAsync("Page.enable", "{}").WaitAsync(TimeSpan.FromSeconds(10), lifetime.Token);
            configureBrowser?.Invoke(core);
            detection = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
            detectionTask = DetectAccount(core, detection.Token);
            ready.TrySetResult(core);
            status.Text = "Loading roblox.com…";
            string navigation = await core.CallDevToolsProtocolMethodAsync("Page.navigate", JsonSerializer.Serialize(new { url = LoginUrl })).WaitAsync(TimeSpan.FromSeconds(30), lifetime.Token);
            using var response = JsonDocument.Parse(navigation);
            if (response.RootElement.TryGetProperty("errorText", out var error)) Error($"Could not load Roblox ({error.GetString()}). Select Retry.");
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (closePending) return;
            Error(ex is TimeoutException ? "Chromium took too long to start or load Roblox. Select Retry." : $"Could not start Roblox sign-in ({ex.GetType().Name}, 0x{ex.HResult:X8}). Select Retry. Check that WebView2 Runtime is installed.");
        }
        finally { initializing = false; if (!closePending) retry.IsEnabled = !verifying; }
    }

    private void Error(string message) { LastError = message; status.Text = message; }
    private static bool Allowed(string url) => Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == "https" && (uri.Host == "roblox.com" || uri.Host.EndsWith(".roblox.com", StringComparison.OrdinalIgnoreCase));
    private async Task ClearProfile()
    {
        try { if (browser.CoreWebView2 is not null) await browser.CoreWebView2.Profile.ClearBrowsingDataAsync(CoreWebView2BrowsingDataKinds.AllProfile).WaitAsync(TimeSpan.FromSeconds(5)); }
        catch { /* The browser may have crashed or may already be shutting down. */ }
    }
    private async Task StopDetection()
    {
        var source = detection;
        var running = detectionTask;
        detection = null; detectionTask = null;
        source?.Cancel();
        try { if (running is not null) await running; }
        finally { source?.Dispose(); }
    }
    private async void ClosingWindow(object? sender, CancelEventArgs e)
    {
        if (allowClose) return;
        e.Cancel = true;
        if (closePending) return;
        closePending = true; retry.IsEnabled = false;
        try { await StopDetection(); await ClearProfile(); }
        finally { allowClose = true; Close(); }
    }
    private async Task DetectAccount(CoreWebView2 core, CancellationToken token)
    {
        string? rejectedCookie = null;
        DateTimeOffset retryAt = DateTimeOffset.MinValue;
        try
        {
            // Stay on WebView2's UI thread. Polling also detects sign-ins completed
            // without a page navigation; only one verification can run at a time.
            while (true)
            {
                await Task.Delay(750, token);
                if (closePending || DateTimeOffset.UtcNow < retryAt) continue;
                var cookies = await core.CookieManager.GetCookiesAsync("https://www.roblox.com").WaitAsync(TimeSpan.FromSeconds(5), token);
                token.ThrowIfCancellationRequested();
                var cookie = cookies.FirstOrDefault(c => c.Name == ".ROBLOSECURITY");
                if (cookie is null || string.IsNullOrEmpty(cookie.Value) || cookie.Value == rejectedCookie) continue;
                verifying = true; retry.IsEnabled = false;
                status.Text = "Verifying your Roblox account…";
                try
                {
                    var verified = await api.Validate(cookie.Value, token);
                    token.ThrowIfCancellationRequested();
                    if (closePending) return;
                    account = verified; accepted = true;
                    // Let this task complete before Closing awaits StopDetection.
                    _ = Dispatcher.BeginInvoke(new Action(Close));
                    return;
                }
                catch (InvalidOperationException)
                {
                    rejectedCookie = cookie.Value;
                    status.Text = "This session could not be verified. Sign in to Roblox again.";
                }
                catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or JsonException or KeyNotFoundException)
                {
                    token.ThrowIfCancellationRequested();
                    retryAt = DateTimeOffset.UtcNow.AddSeconds(5);
                    status.Text = "Could not verify your account. Retrying automatically…";
                }
                finally { verifying = false; if (!closePending) retry.IsEnabled = true; }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception)
        {
            if (!closePending) { retry.IsEnabled = true; Error("Automatic sign-in detection stopped. Select Retry to reopen Roblox."); }
        }
    }
}
