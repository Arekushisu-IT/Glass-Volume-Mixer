using System.ComponentModel;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using GlassMixer.Core;
using Microsoft.Web.WebView2.Core;

namespace GlassMixer.App;

/// <summary>
/// The mixer window. It does three jobs:
///   1. makes itself see-through glass (Native.SetGlass) and draws the blur (BackdropBlur),
///   2. shows the HTML UI from the ui folder in WebView2,
///   3. passes JSON messages between that UI and AudioService.
/// </summary>
public partial class MainWindow : Window
{
    const int HotkeyToggle = 1, HotkeyMinimize = 2;
    const double Inset = 12;        // transparent border around the panel: shadow + resize grip
    const double CornerRadius = 18;  // same as the mockup; the page's --radius must match
    const double MiniWidth = 500 + 2 * Inset, MiniHeight = 52 + 2 * Inset;
    const double FullMinHeight = 240 + 2 * Inset;

    // JsonSerializerDefaults.Web writes camelCase names: MasterVolume -> masterVolume.
    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    readonly AudioService _audio = new();
    readonly DispatcherTimer _meterTimer = new() { Interval = TimeSpan.FromMilliseconds(33) };
    readonly DispatcherTimer _refreshTimer = new() { Interval = TimeSpan.FromSeconds(1.5) };
    readonly DispatcherTimer _saveTimer = new() { Interval = TimeSpan.FromMilliseconds(600) };
    readonly DispatcherTimer _statusTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    readonly BackdropBlur _backdrop;

    IntPtr _hwnd;
    string _lastState = "", _status = "";
    int _tick, _statusTick;
    bool _pinned = true, _minimized, _placed, _quitting;
    Size _fullSize;
    DateTime _hiddenAt;
    WindowSettings? _saved; // last run's position and size, used the first time the window opens

    /// <summary>A one-line summary for the tray tooltip, e.g. "Glass Mixer · Headphones 66%".</summary>
    public event Action<string>? StatusChanged;

    public MainWindow()
    {
        InitializeComponent();
        _saved = WindowSettings.Load();
        if (_saved is { Width: > 0, Height: > 0 })
        {
            Width = _saved.Width;
            Height = _saved.Height;
        }
        _fullSize = new Size(Width, Height);
        _backdrop = new BackdropBlur(this, Backdrop);

        LocationChanged += (_, _) => QueueSave();
        SizeChanged += (_, _) => QueueSave();
        _saveTimer.Tick += (_, _) => { _saveTimer.Stop(); SaveWindow(); };
        _statusTimer.Tick += (_, _) => UpdateStatus();
        _statusTimer.Start();

        // Where the page draws nothing, show the glass behind it instead of white.
        Web.DefaultBackgroundColor = System.Drawing.Color.Transparent;

        SourceInitialized += OnSourceInitialized;
        Loaded += async (_, _) => await InitWebViewAsync();
        Deactivated += (_, _) => { if (!_pinned) HideMixer(); };
        Closing += OnClosing;
        BackdropHost.SizeChanged += (_, _) => ClipBackdrop();

        _meterTimer.Tick += (_, _) => OnMeterTick();
        _refreshTimer.Tick += (_, _) => { _audio.Refresh(); SendState(); };
    }

    // ---------- window setup ----------

    // SourceInitialized is the first moment the window has a Win32 handle (HWND).
    void OnSourceInitialized(object? sender, EventArgs e)
    {
        _hwnd = new WindowInteropHelper(this).Handle;
        var source = HwndSource.FromHwnd(_hwnd);
        source.CompositionTarget.BackgroundColor = Colors.Transparent;
        source.AddHook(WndProc);

        Native.PrepareGlassWindow(_hwnd, Native.IsDarkTheme());
        Native.SetGlass(_hwnd, GlassStyle.Clear); // fully transparent; the page then asks for its blur strength

        const uint mods = Native.MOD_CONTROL | Native.MOD_ALT | Native.MOD_NOREPEAT;
        Native.RegisterHotKey(_hwnd, HotkeyToggle, mods, 'V');
        Native.RegisterHotKey(_hwnd, HotkeyMinimize, mods, 'M');
    }

    // Cuts the blurred picture to the panel's rounded shape. WPF draws geometry clips
    // anti-aliased, which is what makes the corners smooth.
    void ClipBackdrop() =>
        BackdropHost.Clip = new RectangleGeometry(
            new Rect(0, 0, BackdropHost.ActualWidth, BackdropHost.ActualHeight), CornerRadius, CornerRadius);

    /// <summary>
    /// Blur strength from the page's slider, in pixels. 0 means Clear: no blur, no screen capture.
    /// If this Windows can't hide a window from capture, fall back to Windows' fixed blur.
    /// </summary>
    void SetBlur(double radius)
    {
        if (radius <= 0)
        {
            _backdrop.Stop();
            Shadow.Visibility = Visibility.Collapsed;
            Native.SetGlass(_hwnd, GlassStyle.Clear);
            return;
        }
        if (_backdrop.IsRunning) _backdrop.SetRadius(radius);
        else if (!_backdrop.Start(_hwnd, radius))
        {
            Native.SetGlass(_hwnd, GlassStyle.Blur);
            return;
        }
        Shadow.Visibility = Visibility.Visible;
    }

    // Win32 messages that WPF doesn't wrap arrive here. We only need global hotkeys.
    IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == Native.WM_HOTKEY)
        {
            if ((int)wParam == HotkeyToggle) Toggle();
            else if ((int)wParam == HotkeyMinimize)
            {
                if (!IsVisible) ShowNearTray();
                SetMinimized(!_minimized);
            }
            handled = true;
        }
        // After the user drags or resizes it, and when monitors change, make sure the
        // panel is still on a screen (it once ended up above the top of a monitor).
        else if (msg is Native.WM_EXITSIZEMOVE or Native.WM_DISPLAYCHANGE)
        {
            KeepOnScreenSoon();
        }
        return IntPtr.Zero;
    }

    void KeepOnScreen() =>
        Native.KeepOnScreen(_hwnd, (int)Math.Round(Inset * VisualTreeHelper.GetDpi(this).DpiScaleX));

    // Runs after WPF has applied a pending size or position change.
    void KeepOnScreenSoon() => Dispatcher.BeginInvoke(KeepOnScreen, DispatcherPriority.Background);

    async Task InitWebViewAsync()
    {
        // WebView2 keeps cookies, cache and localStorage here.
        var dataFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GlassMixer", "WebView2");
        var env = await CoreWebView2Environment.CreateAsync(userDataFolder: dataFolder);
        await Web.EnsureCoreWebView2Async(env);

        var core = Web.CoreWebView2;
        core.Settings.AreDefaultContextMenusEnabled = false;
        core.Settings.IsZoomControlEnabled = false;
        core.Settings.IsStatusBarEnabled = false;
#if !DEBUG
        core.Settings.AreDevToolsEnabled = false;
#endif
        // Serve the ui folder next to the .exe as https://mixer.local/...
        core.SetVirtualHostNameToFolderMapping(
            "mixer.local", Path.Combine(AppContext.BaseDirectory, "ui"), CoreWebView2HostResourceAccessKind.Deny);
        core.WebMessageReceived += OnWebMessage;
        core.Navigate("https://mixer.local/index.html");
    }

    // ---------- page -> C# ----------

    void OnWebMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        try { HandleWebMessage(e.WebMessageAsJson); }
        catch (Exception ex) { Log.Write($"Message {e.WebMessageAsJson} failed: {ex}"); }
    }

    void HandleWebMessage(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var msg = doc.RootElement;
        string Id() => msg.GetProperty("id").GetString() ?? "";

        switch (msg.GetProperty("type").GetString())
        {
            case "log": // errors and notes from the page's JavaScript
                Log.Write("page: " + msg.GetProperty("text").GetString());
                break;
            case "ready": // the page has loaded and is listening
                Log.Write("page ready");
                SendState(force: true);
                Post(new { type = "minimized", on = _minimized });
                Post(new { type = "pinned", on = _pinned });
                _meterTimer.Start();
                _refreshTimer.Start();
                break;
            case "setVolume":
                _audio.SetVolume(Id(), msg.GetProperty("value").GetSingle());
                break;
            case "setMute":
                _audio.SetMute(Id(), msg.GetProperty("muted").GetBoolean());
                break;
            case "drag":
                Native.BeginSystemMove(_hwnd);
                break;
            case "resize": // edge: "left", "top-left", "bottom-right"...
                Native.BeginSystemResize(_hwnd, msg.GetProperty("edge").GetString() ?? "");
                break;
            case "setBalance": // value: -1 left only ... +1 right only
                _audio.SetBalance(Id(), msg.GetProperty("value").GetSingle());
                break;
            case "setOutput": // deviceId: a device id, or null to follow the Windows default
                var deviceId = msg.GetProperty("deviceId");
                _audio.SetOutput(Id(), deviceId.ValueKind == JsonValueKind.Null ? null : deviceId.GetString());
                SendState(force: true);
                break;
            case "setDevice": // switch the Windows default output device
                _audio.SetDefaultDevice(Id());
                SendState(force: true);
                break;
            case "dock": // double-click on the header: back to the corner above the tray
                _placed = false;
                ShowNearTray();
                break;
            case "blur": // radius in pixels, 0 to 40
                SetBlur(msg.GetProperty("radius").GetDouble());
                break;
#if DEBUG
            case "dumpBackdrop": // debugging: while the blur is on, screenshots can't see the window,
                DumpBackdrop();  // so save what WPF draws (shadow + blur, not the page) to a file
                break;
#endif
            case "size": // the panel's size; the window adds its transparent margin
                ResizeAnchored(msg.GetProperty("w").GetDouble() + 2 * Inset, msg.GetProperty("h").GetDouble() + 2 * Inset);
                break;
            case "minimize":
                SetMinimized(msg.GetProperty("on").GetBoolean());
                break;
            case "pin":
                _pinned = msg.GetProperty("on").GetBoolean();
                Topmost = _pinned;
                break;
            case "hide":
                HideMixer();
                break;
        }
    }

#if DEBUG
    void DumpBackdrop()
    {
        var root = (FrameworkElement)Content;
        double scale = VisualTreeHelper.GetDpi(this).DpiScaleX;
        var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(
            (int)(root.ActualWidth * scale), (int)(root.ActualHeight * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        bitmap.Render(root);
        var png = new System.Windows.Media.Imaging.PngBitmapEncoder();
        png.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
        var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GlassMixer", "backdrop.png");
        using var file = File.Create(path);
        png.Save(file);
        Log.Write("backdrop saved to " + path);
    }
#endif

    // ---------- C# -> page ----------

    void Post(object message) =>
        Web.CoreWebView2?.PostWebMessageAsJson(JsonSerializer.Serialize(message, Json));

    // Sends the full app list, but only when something changed since last time.
    void SendState(bool force = false)
    {
        var snap = _audio.GetSnapshot();
        // Shape each app for the page: add its .exe file name and icon, drop what the page doesn't use.
        var apps = snap.Apps.Select(a => new
        {
            a.Id, a.Name, a.Volume, a.Muted, a.IsSystem, a.Active, a.Balance, a.OutputId,
            Exe = a.ExePath is null ? null : Path.GetFileName(a.ExePath),
            Icon = IconCache.DataUrl(a.ExePath),
        });
        var json = JsonSerializer.Serialize(
            new { type = "state", snap.DeviceId, snap.DeviceName, snap.Devices, snap.MasterVolume, snap.MasterMuted, apps,
                  snap.PerAppOutputSupported }, Json);
        if (!force && json == _lastState) return;
        _lastState = json;
        Web.CoreWebView2?.PostWebMessageAsJson(json);
        Log.Write($"state sent: {snap.Apps.Count} apps on {snap.DeviceName}");
    }

    void OnMeterTick()
    {
        if (!IsVisible) return;
        Post(new { type = "levels", master = _audio.MasterPeak, apps = _audio.GetPeaks() });

        // About 3 times a second, also check for volume changes made outside Glass Mixer.
        if (++_tick % 10 == 0) SendState();
    }

    // ---------- showing, hiding, sizing ----------

    public void ShowNearTray()
    {
        bool firstTime = !_placed;
        if (firstTime)
        {
            if (_saved is not null && IsOnScreen(_saved))
            {
                Left = _saved.Right - Width; // back where it was last time
                Top = _saved.Bottom - Height;
            }
            else
            {
                var area = SystemParameters.WorkArea; // the main screen minus the taskbar
                Left = area.Right - Width;           // the transparent margin already leaves a gap
                Top = area.Bottom - Height;
            }
            _placed = true;
        }
        Show();
        Activate();
        _saved = null;
        KeepOnScreen();
        Post(new { type = "shown" }); // the page plays the mockup's pop-in
    }

    /// <summary>
    /// For when you open Glass Mixer yourself (Start menu, desktop icon, or opening it again
    /// while it runs hidden): always the full panel, never the small minimized bar, which
    /// is easy to miss, especially on another monitor.
    /// </summary>
    public void OpenFull()
    {
        ShowNearTray();
        if (_minimized) SetMinimized(false);
    }

    // A quick first check of a saved position; KeepOnScreen then fixes anything that's
    // only partly visible, or in a gap between monitors of different sizes.
    static bool IsOnScreen(WindowSettings s)
    {
        var desktop = new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop,
                               SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
        return desktop.Contains(new Point(s.Right - 60, s.Bottom - 60));
    }

    // Moving or resizing fires many events; save once things settle.
    void QueueSave()
    {
        if (!_placed) return;
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    void SaveWindow()
    {
        if (!_placed) return;
        new WindowSettings
        {
            Right = Left + Width,
            Bottom = Top + Height,
            Width = _minimized ? _fullSize.Width : Width,
            Height = _minimized ? _fullSize.Height : Height,
            Minimized = _minimized,
        }.Save();
    }

    // The tray tooltip. Before the page has loaded (started at sign-in), keep the session
    // list fresh here every few seconds, since the page's refresh timer isn't running yet.
    void UpdateStatus()
    {
        if (!_refreshTimer.IsEnabled && ++_statusTick % 3 == 0) _audio.Refresh();
        string device = _audio.DeviceName;
        int cut = device.IndexOf(" (", StringComparison.Ordinal);
        if (cut > 0) device = device[..cut];
        string status = $"Glass Mixer · {device} {(_audio.MasterMuted ? "muted" : $"{Math.Round(_audio.MasterVolume * 100)}%")}";
        if (status == _status) return;
        _status = status;
        Log.Write("tray: " + status);
        StatusChanged?.Invoke(status);
    }

    public void Toggle()
    {
        if (IsVisible) HideMixer();
        // Clicking the tray icon first takes focus away (which may hide the window),
        // then sends the click. Ignore a click that comes right after that hide.
        else if ((DateTime.Now - _hiddenAt).TotalMilliseconds > 300) ShowNearTray();
    }

    /// <summary>Raised when the window hides (✕, Ctrl+Alt+V, clicking away). The app keeps running in the tray.</summary>
    public event Action? HiddenToTray;

    void HideMixer()
    {
        _hiddenAt = DateTime.Now;
        Hide();
        HiddenToTray?.Invoke();
    }

    void SetMinimized(bool on)
    {
        if (on != _minimized)
        {
            _minimized = on;
            if (on)
            {
                _fullSize = new Size(Width, Height);
                MinHeight = 0;
                ResizeAnchored(MiniWidth, MiniHeight);
            }
            else
            {
                ResizeAnchored(_fullSize.Width, _fullSize.Height);
                MinHeight = FullMinHeight;
            }
        }
        Post(new { type = "minimized", on = _minimized });
    }

    // Changes size while keeping the bottom-right corner in place, like a tray flyout.
    // (No clamping to the main screen's work area: the window may be on another monitor.)
    void ResizeAnchored(double width, double height)
    {
        double right = Left + Width, bottom = Top + Height;
        Width = Math.Min(width, SystemParameters.VirtualScreenWidth);
        Height = Math.Min(height, SystemParameters.VirtualScreenHeight);
        Left = right - Width;
        Top = bottom - Height;
        KeepOnScreenSoon(); // growing upward near the top of a screen must not push it off
    }

    // ---------- closing ----------

    public void CloseForQuit()
    {
        _quitting = true;
        Close();
    }

    void OnClosing(object? sender, CancelEventArgs e)
    {
        if (!_quitting) // Alt+F4 only hides; Quit is in the tray menu
        {
            e.Cancel = true;
            HideMixer();
            return;
        }
        SaveWindow();
        _meterTimer.Stop();
        _refreshTimer.Stop();
        _statusTimer.Stop();
        _saveTimer.Stop();
        Native.UnregisterHotKey(_hwnd, HotkeyToggle);
        Native.UnregisterHotKey(_hwnd, HotkeyMinimize);
        _backdrop.Dispose();
        _audio.Dispose();
    }
}
