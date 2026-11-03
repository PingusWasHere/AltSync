using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using AltSync.Core;

namespace AltSync;

public sealed class CookieImportWindow : Window
{
    private readonly RobloxApi api;
    private readonly PasswordBox input = new() { FontSize = 13, Padding = new Thickness(11), Background = new SolidColorBrush(Color.FromRgb(20, 20, 20)), Foreground = Brushes.WhiteSmoke, BorderBrush = new SolidColorBrush(Color.FromRgb(65, 65, 65)), MaxLength = 16420 };
    private readonly TextBlock status = new() { Text = "The session is verified with Roblox and stored encrypted on this Windows account.", Foreground = Brushes.Gray, TextWrapping = TextWrapping.Wrap, FontSize = 12, Margin = new Thickness(0, 12, 0, 0) };
    private readonly Button import = new() { Content = "Add account", Padding = new Thickness(16, 8, 16, 8), IsEnabled = false };
    private readonly TaskCompletionSource<Account?> result = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly CancellationTokenSource lifetime = new();
    private bool verifying, closing;
    private Account? account;
    public CookieImportWindow(Window owner) : this(owner, new RobloxApi()) { }
    internal CookieImportWindow(Window owner, RobloxApi api)
    {
        this.api = api;
        Owner = owner; Icon = owner.Icon; Title = "AltSync · Import cookie";
        Width = 500; SizeToContent = SizeToContent.Height; ResizeMode = ResizeMode.NoResize; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = new SolidColorBrush(Color.FromRgb(25, 25, 25)); Foreground = Brushes.WhiteSmoke;
        var panel = new StackPanel { Margin = new Thickness(24) };
        panel.Children.Add(new TextBlock { Text = "Import an account", FontSize = 19, Margin = new Thickness(0, 0, 0, 12) });
        panel.Children.Add(new TextBlock { Text = "Paste the .ROBLOSECURITY cookie value for your Roblox account.", FontSize = 12, TextWrapping = TextWrapping.Wrap, Foreground = Brushes.LightGray, Margin = new Thickness(0, 0, 0, 12) });
        panel.Children.Add(input); panel.Children.Add(status);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 22, 0, 0) };
        var cancel = new Button { Content = "Cancel", Padding = new Thickness(16, 8, 16, 8), Margin = new Thickness(0, 0, 8, 0) };
        foreach (var button in new[] { cancel, import })
        {
            button.Background = new SolidColorBrush(Color.FromRgb(42, 42, 42)); button.Foreground = Brushes.WhiteSmoke;
            button.BorderBrush = new SolidColorBrush(Color.FromRgb(65, 65, 65));
            button.Style = (Style)System.Windows.Markup.XamlReader.Parse("""
                <Style xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" TargetType="Button">
                  <Setter Property="Template"><Setter.Value><ControlTemplate TargetType="Button">
                    <Border x:Name="surface" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" Background="{TemplateBinding Background}" BorderBrush="{TemplateBinding BorderBrush}" BorderThickness="1" CornerRadius="6" Padding="{TemplateBinding Padding}"><ContentPresenter HorizontalAlignment="Center" VerticalAlignment="Center"/></Border>
                    <ControlTemplate.Triggers>
                      <Trigger Property="IsMouseOver" Value="True"><Setter TargetName="surface" Property="Background" Value="#383838"/></Trigger>
                      <Trigger Property="IsEnabled" Value="False"><Setter Property="Opacity" Value="0.45"/></Trigger>
                    </ControlTemplate.Triggers>
                  </ControlTemplate></Setter.Value></Setter>
                </Style>
                """);
        }
        actions.Children.Add(cancel); actions.Children.Add(import); panel.Children.Add(actions); Content = panel;
        input.PasswordChanged += (_, _) => import.IsEnabled = !verifying && input.Password.Length >= 32;
        import.Click += async (_, _) => await Import();
        cancel.Click += (_, _) => Close();
        input.KeyDown += async (_, e) => { if (e.Key == System.Windows.Input.Key.Enter && import.IsEnabled) { e.Handled = true; await Import(); } };
        PreviewKeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Escape) { e.Handled = true; Close(); } };
        Loaded += (_, _) => input.Focus();
        Closing += (_, _) => { closing = true; lifetime.Cancel(); input.Clear(); };
        Closed += (_, _) =>
        {
            if (Owner is { IsVisible: true } ownerWindow) { ownerWindow.IsEnabled = true; ownerWindow.Activate(); }
            result.TrySetResult(account);
        };
    }
    public async Task<Account?> ShowForAccountAsync()
    {
        Owner.IsEnabled = false;
        try { Show(); return await result.Task; }
        finally { if (Owner.IsVisible) Owner.IsEnabled = true; }
    }
    private async Task Import()
    {
        if (verifying || closing) return;
        verifying = true; import.IsEnabled = false; input.IsEnabled = false;
        try
        {
            string cookie = CookieSession.Parse(input.Password);
            status.Text = "Verifying your Roblox account…";
            var verified = await api.Validate(cookie, lifetime.Token);
            if (closing) return;
            account = verified; input.Clear(); Close();
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (ArgumentException) { if (!closing) status.Text = "Paste only the .ROBLOSECURITY value, without other cookies or headers."; }
        catch (InvalidOperationException) { if (!closing) status.Text = "This session has expired or could not be verified. Sign in with Roblox or use a current cookie."; }
        catch (Exception) { if (!closing) status.Text = "Could not verify the account. Check your connection and try again."; }
        finally { verifying = false; if (!closing) { import.IsEnabled = input.Password.Length >= 32; input.IsEnabled = true; } }
    }
}
