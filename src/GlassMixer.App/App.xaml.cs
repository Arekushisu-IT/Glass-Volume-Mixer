using System.Windows;

namespace GlassMixer.App;

/// <summary>
/// Startup for a tray app: one copy only, a tray icon, and a window that hides
/// instead of closing. The app ends only from the tray menu's Quit.
/// </summary>
public partial class App : Application
{
    const string ShowSignalName = @"Local\GlassMixer.Show";

    Mutex? _singleInstance;
    EventWaitHandle? _showSignal;
    RegisteredWaitHandle? _showWait;
    MainWindow? _window;
    TrayIcon? _tray;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Any exception nobody caught on the UI thread: write it down instead of crashing.
        DispatcherUnhandledException += (_, args) =>
        {
            Log.Write("Unhandled: " + args.Exception);
            args.Handled = true;
        };
        Log.Write("Glass Mixer starting");

        bool atSignIn = e.Args.Contains(StartWithWindows.Argument);

        // A named mutex is a lock shared across processes. If it already exists,
        // Glass Mixer is already running (maybe hidden in the tray). Opening it again from
        // the Start menu should bring that copy up, so ring its "show" signal, then quit.
        _singleInstance = new Mutex(initiallyOwned: true, @"Local\GlassMixer.SingleInstance", out bool isFirst);
        if (!isFirst)
        {
            if (!atSignIn && EventWaitHandle.TryOpenExisting(ShowSignalName, out var show))
            {
                Native.AllowSetForegroundWindow(Native.ASFW_ANY); // let the other copy come to the front
                show.Set();
                show.Dispose();
            }
            Log.Write("Already running: asked the running copy to show itself");
            Shutdown();
            return;
        }

        _window = new MainWindow();
        _tray = new TrayIcon(onToggle: _window.Toggle, onQuit: Quit);
        _window.StatusChanged += _tray.SetTooltip;
        _window.HiddenToTray += _tray.ShowStillRunningHintOnce;

        // Listen for the signal above. The wait runs on a background thread, so hop back
        // to the UI thread (Dispatcher) before touching the window.
        _showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, ShowSignalName);
        _showWait = ThreadPool.RegisterWaitForSingleObject(_showSignal,
            (_, _) => Dispatcher.BeginInvoke(() => _window?.ShowNearTray()), null, Timeout.Infinite, executeOnlyOnce: false);

        // If "Start with Windows" points at a copy that was moved or deleted, point it here.
        StartWithWindows.RepairPath();

        if (atSignIn)
        {
            // Started by Windows at sign-in: wait in the tray. EnsureHandle creates the
            // window without showing it, so the Ctrl+Alt+V hotkey works straight away.
            new System.Windows.Interop.WindowInteropHelper(_window).EnsureHandle();
        }
        else
        {
            _window.ShowNearTray();
        }
    }

    void Quit()
    {
        _tray?.Dispose();
        _window?.CloseForQuit();
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _tray?.Dispose();
        _showWait?.Unregister(null);
        _showSignal?.Dispose();
        _singleInstance?.Dispose();
        base.OnExit(e);
    }
}
