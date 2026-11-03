using System.Windows;

namespace AltSync;

public partial class App : Application
{
    private Mutex? instance;
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        bool preview = e.Args.Contains("--smoke-test") || e.Args.Contains("--login-smoke-test") || e.Args.Contains("--power-smoke-test");
        instance = new Mutex(true, preview ? "Local\\AltSync.Preview." + Environment.ProcessId : "Local\\AltSync.Desktop", out bool first);
        if (!first) { MessageBox.Show("AltSync is already running.", "AltSync"); Shutdown(); return; }
        if (!preview && AltSync.Core.AutoUpdater.TryApplyPending()) { Shutdown(); return; }
        DispatcherUnhandledException += (_, args) => {
            MessageBox.Show("AltSync encountered an error. " + args.Exception.Message, "AltSync");
            args.Handled = true;
        };
        MainWindow = new MainWindow(e.Args.Contains("--smoke-test"), e.Args.Contains("--login-smoke-test"), e.Args.Contains("--power-smoke-test"));
        MainWindow.Show();
    }
    protected override void OnExit(ExitEventArgs e) { instance?.Dispose(); base.OnExit(e); }
}
