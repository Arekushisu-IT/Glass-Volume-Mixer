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
/// The mixer window. It lives at the top, left or right edge of a screen and has two shapes:
///   panel   the whole mixer, at a spot along the edge that you choose by dragging its header
///   pill    a small glass pill at the edge, while the mixer is tucked away
/// When your mouse leaves the panel it tucks away into the pill; hovering the pill (or
/// touching the screen edge) brings it back. The page draws both shapes; this class sizes
/// and places the window for each, in physical pixels (see Native, "screen edges").
///
/// It also: makes itself see-through glass with a blur behind (Native.SetGlass, GlassBackdrop),
/// shows the HTML UI from the ui folder in WebView2, and passes JSON messages between that UI
/// and AudioService, MediaWatcher and FullScreen.
/// </summary>
public partial class MainWindow : Window
{
    const int HotkeyToggle = 1, HotkeyMic = 2;
    const double Inset = 20;       // transparent border around the panel or pill: room for the shadow
    const double Gap = 12;         // the panel's distance from the screen edge, as in the mockup
    const double PillGap = 6;      // the pill's distance from the screen edge

    // JsonSerializerDefaults.Web writes camelCase names: MasterVolume -> masterVolume.
    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    enum Shape { Panel, Pill }

    readonly AudioService _audio = new();
    readonly MediaWatcher _media = new();
    readonly DispatcherTimer _meterTimer = new() { Interval = TimeSpan.FromMilliseconds(33) };
    readonly DispatcherTimer _refreshTimer = new() { Interval = TimeSpan.FromSeconds(1.5) };
    readonly DispatcherTimer _statusTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    readonly DispatcherTimer _probeTimer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    readonly DispatcherTimer _mouseTimer = new() { Interval = TimeSpan.FromMilliseconds(60) };
    readonly DispatcherTimer _fullTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    readonly DispatcherTimer _mediaTimer = new() { Interval = TimeSpan.FromMilliseconds(150) };
    readonly GlassBackdrop _glass = new();
    readonly WindowSettings _settings = WindowSettings.Load();
    DockHint? _hint;

    IntPtr _hwnd;
    string _lastState = "", _lastMic = "", _lastMedia = "", _status = "";
    int _statusTick, _mediaTick;
    bool _pageReady, _quitting;

    // ----- where it is and what shape -----
    Shape _shape = Shape.Pill;
    Native.Monitor _monitor;
    double _contentHeight = 460;            // the panel's natural height, from the page (scaled units)
    double _pillW = 124, _pillH = 32, _pillR = 16;
    double _radius = 18;                    // corner radius of what's showing (scaled units)
    double _blur;                           // the blur slider's value, in pixels at 100% scaling
    bool _glassOn;                          // false while the page swaps shapes, so no empty blur shows
    double _backdropLight = -1;

    // ----- tucking -----
    bool _autoTuck = true;     // the page's "Tuck away when the mouse leaves"
    bool _busy;                // a drop-down list is open in the page
    bool _moving;              // you're dragging it
    bool _hoveredSinceOpen;    // opened from the tray or a hotkey: stay until the mouse has been and gone
    bool _hiddenForFullScreen;
    DateTime? _leftAt, _pillHoverAt, _edgeAt;
    DateTime _lastPillWheel;
    FullScreen.Kind _fullScreen;
    (string Edge, double Along, Native.Monitor Monitor)? _dragTarget;

    /// <summary>A one-line summary for the tray tooltip, e.g. "Glass Mixer · Headphones 66%".</summary>
    public event Action<string>? StatusChanged;

    public MainWindow()
    {
        InitializeComponent();
        _monitor = _settings.HasMonitor ? Native.MonitorAt(_settings.MonitorX, _settings.MonitorY) : PrimaryMonitor();
        Log.Write($"at the {_settings.Edge} edge, {_settings.Along:P0} along, of the screen at {_monitor.Bounds}");

        _statusTimer.Tick += (_, _) => UpdateStatus();
        _statusTimer.Start();
        _probeTimer.Tick += (_, _) => ProbeBackdrop();
        _mouseTimer.Tick += (_, _) => OnMouseTick();
        _fullTimer.Tick += (_, _) => OnFullScreenTick();
        _mediaTimer.Tick += (_, _) => { _mediaTimer.Stop(); SendMedia(); };

        // Where the page draws nothing, show the glass behind it instead of white.
        Web.DefaultBackgroundColor = System.Drawing.Color.Transparent;

        SourceInitialized += OnSourceInitialized;
        Loaded += async (_, _) => await InitWebViewAsync();
        Closing += OnClosing;
        SizeChanged += (_, _) => UpdateGlass();
        // Moved to a screen with different scaling: WPF resizes the window for it; put it right again.
        DpiChanged += (_, _) => Dispatcher.BeginInvoke(PlaceWindow, DispatcherPriority.Background);
        Deactivated += (_, _) => OnDeactivated();

        _meterTimer.Tick += (_, _) => OnMeterTick();
        _refreshTimer.Tick += (_, _) => { _audio.Refresh(); SendState(); SendMic(); };

        // The volume keys (or anything else) changed the volume: tell the page right away,
        // so the pill can show it. Windows calls this on a background thread.
        _audio.MasterChanged += (volume, muted) =>
            Dispatcher.BeginInvoke(() => Post(new { type = "volume", masterVolume = volume, masterMuted = muted }));
        // Media events come in bursts (title, cover, playback): send once things settle.
        _media.Changed += () => Dispatcher.BeginInvoke(() => { _mediaTimer.Stop(); _mediaTimer.Start(); });
    }

    // The main screen (the one with the Start button by default), found by its centre.
    static Native.Monitor PrimaryMonitor()
    {
        var b = System.Windows.Forms.Screen.PrimaryScreen!.Bounds;
        return Native.MonitorAt(b.Left + b.Width / 2, b.Top + b.Height / 2);
    }

    // ---------- starting ----------

    /// <summary>Shows the mixer: open at its edge, or (at sign-in) tucked away into the pill.</summary>
    public void Start(bool open)
    {
        _shape = open ? Shape.Panel : Shape.Pill;
        _hoveredSinceOpen = false;
        Show();
        PlaceWindow();
        if (open) Activate();
    }

    // SourceInitialized is the first moment the window has a Win32 handle (HWND).
    void OnSourceInitialized(object? sender, EventArgs e)
    {
        _hwnd = new WindowInteropHelper(this).Handle;
        var source = HwndSource.FromHwnd(_hwnd);
        source.CompositionTarget.BackgroundColor = Colors.Transparent;
        source.AddHook(WndProc);

        Native.PrepareGlassWindow(_hwnd, Native.IsDarkTheme());
        Native.SetGlass(_hwnd, GlassStyle.Clear); // fully transparent; the page then asks for its blur strength
        _glass.Attach(_hwnd);

        const uint mods = Native.MOD_CONTROL | Native.MOD_ALT | Native.MOD_NOREPEAT;
        Native.RegisterHotKey(_hwnd, HotkeyToggle, mods, 'V');
        Native.RegisterHotKey(_hwnd, HotkeyMic, mods, 'K');
    }

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
        await _media.StartAsync();
    }

    // ---------- the blur ----------

    /// <summary>Blur strength from the page's slider, in pixels. 0 means Clear: no blur at all.</summary>
    void SetBlur(double radius)
    {
        _blur = Math.Max(0, radius);
        if (!_glass.IsAttached)
        {
            // An older Windows without the composition blur: use Windows' fixed-strength blur.
            Native.SetGlass(_hwnd, _blur > 0 ? GlassStyle.Blur : GlassStyle.Clear);
            return;
        }
        UpdateGlass();
    }

    // Keeps the blur and the shadow exactly under the panel or pill. Composition works in
    // physical pixels, WPF in scaled units, so everything is multiplied by the screen's scaling.
    void UpdateGlass()
    {
        bool on = _glassOn && _blur > 0;
        Shadow.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        Shadow.CornerRadius = new CornerRadius(Math.Max(0, _radius - 1));
        if (!_glass.IsAttached || ActualWidth <= 0) return;
        double scale = VisualTreeHelper.GetDpi(this).DpiScaleX;
        float inset = (float)Math.Round(Inset * scale);
        _glass.SetBounds(inset, inset,
            (float)Math.Round(ActualWidth * scale) - 2 * inset, (float)Math.Round(ActualHeight * scale) - 2 * inset,
            (float)(_radius * scale));
        _glass.SetBlur(on ? (float)(_blur * scale) : 0);
    }

    // ---------- geometry: where the panel and the pill go ----------

    // The panel's rectangle on the current edge, without the transparent margin.
    Native.PxRect PanelRect(string edge, double along, Native.Monitor m)
    {
        var work = m.Work;
        double s = m.Scale, gap = Gap * s;
        double availW = work.Width / s, availH = work.Height / s;
        double w, h;
        if (edge == "top")
        {
            // Wide and short: apps spread into columns, so it covers only a band of the screen.
            w = Math.Min(860, availW - 32);
            h = Math.Min(_contentHeight, Math.Min(520, availH - 76));
        }
        else
        {
            w = Math.Min(330, availW - 32);
            h = Math.Min(_contentHeight, availH - 2 * Gap);
        }
        int pw = (int)Math.Round(w * s), ph = (int)Math.Round(h * s);
        int x, y;
        if (edge == "top")
        {
            x = (int)Math.Clamp(work.Left + work.Width * along - pw / 2.0, work.Left + gap, Math.Max(work.Left + gap, work.Right - pw - gap));
            y = (int)(work.Top + gap);
        }
        else
        {
            y = (int)Math.Clamp(work.Top + work.Height * along - ph / 2.0, work.Top + gap, Math.Max(work.Top + gap, work.Bottom - ph - gap));
            x = edge == "left" ? (int)(work.Left + gap) : (int)(work.Right - gap - pw);
        }
        return new Native.PxRect(x, y, x + pw, y + ph);
    }

    // The pill sits at the edge, at the middle of where the panel is.
    Native.PxRect PillRect()
    {
        var m = _monitor; double s = m.Scale;
        var panel = PanelRect(_settings.Edge, _settings.Along, m);
        int w = (int)Math.Round(_pillW * s), h = (int)Math.Round(_pillH * s), gap = (int)Math.Round(PillGap * s);
        return _settings.Edge switch
        {
            "top" => Offset(panel.Left + panel.Width / 2 - w / 2, m.Work.Top + gap, w, h),
            "left" => Offset(m.Work.Left + gap, panel.Top + panel.Height / 2 - h / 2, w, h),
            _ => Offset(m.Work.Right - gap - w, panel.Top + panel.Height / 2 - h / 2, w, h),
        };
    }

    static Native.PxRect Offset(int x, int y, int w, int h) => new(x, y, x + w, y + h);

    static Native.PxRect Inflate(Native.PxRect r, int by) => new(r.Left - by, r.Top - by, r.Right + by, r.Bottom + by);

    /// <summary>Sizes and places the window for the current shape. Safe to call any time.</summary>
    void PlaceWindow()
    {
        if (_hwnd == IntPtr.Zero || _moving) return;
        _radius = _shape == Shape.Panel ? 18 : _pillR;
        var inner = _shape == Shape.Panel ? PanelRect(_settings.Edge, _settings.Along, _monitor) : PillRect();
        var outer = Inflate(inner, (int)Math.Round(Inset * _monitor.Scale));
        if (Native.WindowRect(_hwnd) != outer) Native.Place(_hwnd, outer);
        UpdateGlass();
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
                _pageReady = true;
                SendState(force: true);
                SendMic(force: true);
                SendMedia();
                Post(new { type = "game", on = _fullScreen == FullScreen.Kind.Game });
                _meterTimer.Start();
                _refreshTimer.Start();
                _mouseTimer.Start();
                _fullTimer.Start();
                _backdropLight = -1; // send the brightness again to the freshly loaded page
                _probeTimer.Start();
                SetShape(_shape, force: true);
                break;
            case "setVolume":
                _audio.SetVolume(Id(), msg.GetProperty("value").GetSingle());
                if (_shape == Shape.Pill) _lastPillWheel = DateTime.Now; // scrolling over the pill: don't open
                break;
            case "setMute":
                _audio.SetMute(Id(), msg.GetProperty("muted").GetBoolean());
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
            case "setMicMute":
                _audio.SetMicMute(msg.GetProperty("muted").GetBoolean());
                SendMic();
                break;
            case "media": // id: the app, action: "toggle", "next" or "prev"
                _ = _media.CommandAsync(Id(), msg.GetProperty("action").GetString() ?? "");
                break;
            case "blur": // radius in pixels, 0 to 40
                SetBlur(msg.GetProperty("radius").GetDouble());
                break;
            case "autoTuck": // "Tuck away when the mouse leaves"
                _autoTuck = msg.GetProperty("on").GetBoolean();
                break;
            case "busy": // a drop-down list opened or closed: don't tuck away under it
                _busy = msg.GetProperty("on").GetBoolean();
                break;

            case "drag": // the header: move it to another spot or edge
                Drag();
                break;
            case "content": // the panel's natural height: the window fits it, up to the screen's limit
                _contentHeight = Math.Max(120, msg.GetProperty("h").GetDouble());
                if (_shape == Shape.Panel) PlaceWindow();
                break;
            case "pill": // the pill's size: compact, with cover art, or grown for a message
                _pillW = msg.GetProperty("w").GetDouble();
                _pillH = msg.GetProperty("h").GetDouble();
                _pillR = msg.GetProperty("r").GetDouble();
                if (_shape == Shape.Pill) { PlaceWindow(); Post(new { type = "placed", shape = "pill" }); }
                break;
            case "shapeReady": // the page has cleared the old shape: place the window, then it shows the new one
                PlaceWindow();
                _glassOn = true;
                UpdateGlass();
                Post(new { type = "placed", shape = _shape == Shape.Panel ? "panel" : "pill" });
                break;
            case "peek": // the pill was hovered or clicked
                Peek(activate: msg.TryGetProperty("click", out var click) && click.GetBoolean());
                break;
            case "tuck": // the tuck button or Escape
                Tuck();
                break;
        }
    }

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

    void SendMic(bool force = false)
    {
        var json = JsonSerializer.Serialize(new { type = "mic", mic = _audio.Mic }, Json);
        if (!force && json == _lastMic) return;
        _lastMic = json;
        Web.CoreWebView2?.PostWebMessageAsJson(json);
    }

    void SendMedia()
    {
        if (!_pageReady) return;
        var (tracks, current) = _media.Snapshot();
        var json = JsonSerializer.Serialize(new { type = "media", tracks, current }, Json);
        if (json == _lastMedia) return;
        _lastMedia = json;
        Web.CoreWebView2?.PostWebMessageAsJson(json);
    }

    // Adaptive contrast: tell the page how bright the screen behind the mixer is,
    // whenever that changes noticeably. The page then adds tint only if its text needs it.
    void ProbeBackdrop()
    {
        if (!IsVisible) return;
        if (BackdropProbe.Sample(_hwnd) is not double light || Math.Abs(light - _backdropLight) < 0.03) return;
        _backdropLight = light;
        Post(new { type = "backdrop", light = Math.Round(light, 3) });
    }

    void OnMeterTick()
    {
        if (!IsVisible) return;
        // The pill only needs the master level for its bars.
        if (_shape == Shape.Pill) Post(new { type = "levels", master = _audio.MasterPeak, apps = new { }, mic = 0f });
        else Post(new { type = "levels", master = _audio.MasterPeak, apps = _audio.GetPeaks(), mic = _audio.MicPeak });

        // About 3 times a second, also check for volume changes made outside Glass Mixer.
        if (++_mediaTick % 10 == 0) SendState();
        // Now Playing's position: the page counts the seconds itself; a fresh reading now and then.
        if (_mediaTick % 90 == 0) SendMedia();
    }

    // ---------- the panel and the pill ----------

    // Swapping shapes takes one round trip: the page clears what it shows ("shapeReady"),
    // the window changes size and place, then the page shows the new shape ("placed").
    // That way the panel never flashes at the pill's size, or the other way round.
    void SetShape(Shape shape, bool force = false)
    {
        if (shape == _shape && !force) return;
        _shape = shape;
        _leftAt = _pillHoverAt = _edgeAt = null;
        if (!_pageReady) { PlaceWindow(); return; }
        _glassOn = false;
        UpdateGlass();
        Post(new { type = "shape", shape = shape == Shape.Panel ? "panel" : "pill", edge = _settings.Edge });
    }

    /// <summary>Brings the panel out. activate: from the tray, a hotkey or a click, so it takes the keyboard too.</summary>
    void Peek(bool activate)
    {
        if (_hiddenForFullScreen) { _hiddenForFullScreen = false; Show(); }
        if (_shape != Shape.Panel)
        {
            _hoveredSinceOpen = !activate && CursorInside(); // hovered open: tucks once the mouse leaves
            SetShape(Shape.Panel);
        }
        if (activate) { _hoveredSinceOpen = false; Activate(); }
    }

    void Tuck()
    {
        if (_shape == Shape.Pill || _moving) return;
        SetShape(Shape.Pill);
    }

    /// <summary>The tray icon and Ctrl+Alt+V: open it, or tuck it away.</summary>
    public void Toggle()
    {
        if (_shape == Shape.Panel && IsVisible && !_hiddenForFullScreen) Tuck();
        else Peek(activate: true);
    }

    /// <summary>Opened again from the Start menu or desktop icon while it runs: show the panel.</summary>
    public void OpenFull() => Peek(activate: true);

    bool CursorInside()
    {
        var (x, y) = Native.CursorPos();
        return Native.WindowRect(_hwnd).Contains(x, y);
    }

    // Watches the mouse ~16 times a second: tuck away after it leaves the panel, open when it
    // rests on the pill or touches the screen edge next to it.
    void OnMouseTick()
    {
        if (_hwnd == IntPtr.Zero || !IsVisible || _moving) return;
        var (x, y) = Native.CursorPos();
        var now = DateTime.Now;
        var win = Native.WindowRect(_hwnd);
        int inset = (int)Math.Round(Inset * _monitor.Scale);

        if (_shape == Shape.Panel)
        {
            if (win.Contains(x, y)) { _hoveredSinceOpen = true; _leftAt = null; return; }
            if (!_hoveredSinceOpen || !_autoTuck || _busy || Native.LeftButtonDown()) { _leftAt = null; return; }
            _leftAt ??= now;
            if ((now - _leftAt.Value).TotalMilliseconds > 650) Tuck();
            return;
        }

        // The pill (without its transparent margin): rest on it for a moment to open the mixer.
        var pill = Inflate(win, -inset);
        if (pill.Contains(x, y) && (now - _lastPillWheel).TotalMilliseconds > 900)
        {
            _pillHoverAt ??= now;
            if ((now - _pillHoverAt.Value).TotalMilliseconds > 280) Peek(activate: false);
        }
        else _pillHoverAt = null;

        // The screen edge along the mixer's spot, like a hot corner.
        var panel = PanelRect(_settings.Edge, _settings.Along, _monitor);
        var work = _monitor.Work;
        bool atEdge = _settings.Edge switch
        {
            "top" => y <= work.Top + 1 && x >= panel.Left && x < panel.Right,
            "left" => x <= work.Left + 1 && y >= panel.Top && y < panel.Bottom,
            _ => x >= work.Right - 2 && y >= panel.Top && y < panel.Bottom,
        };
        if (atEdge && !Native.LeftButtonDown())
        {
            _edgeAt ??= now;
            if ((now - _edgeAt.Value).TotalMilliseconds > 120) Peek(activate: false);
        }
        else _edgeAt = null;
    }

    // Clicked somewhere else: tuck away, unless a drop-down list of the page took the focus.
    void OnDeactivated()
    {
        if (_shape != Shape.Panel || !_autoTuck) return;
        Dispatcher.BeginInvoke(async () =>
        {
            await Task.Delay(200);
            if (!IsActive && !_busy && !_moving && !CursorInside()) Tuck();
        });
    }

    // A full-screen app in front (a game, a video): the Gaming profile can switch on,
    // and the pill steps out of the way until it's gone.
    void OnFullScreenTick()
    {
        var kind = FullScreen.Check();
        if (kind != _fullScreen)
        {
            bool wasGame = _fullScreen == FullScreen.Kind.Game;
            _fullScreen = kind;
            if (wasGame != (kind == FullScreen.Kind.Game)) Post(new { type = "game", on = kind == FullScreen.Kind.Game });
        }
        bool hide = kind != FullScreen.Kind.None && !IsActive && _shape == Shape.Pill;
        if (hide && !_hiddenForFullScreen) { _hiddenForFullScreen = true; Hide(); }
        else if (kind == FullScreen.Kind.None && _hiddenForFullScreen) { _hiddenForFullScreen = false; Show(); PlaceWindow(); }
    }

    // ---------- moving it: drag the header to another spot or edge ----------

    void Drag()
    {
        if (_shape != Shape.Panel) return;
        _moving = true;
        _dragTarget = null;
        _hint ??= new DockHint();
        try
        {
            // Windows runs its own move loop here and returns when you let go.
            // WM_MOVING (in WndProc) updates the drop hint as it goes.
            Native.BeginSystemMove(_hwnd);
        }
        finally
        {
            _moving = false;
            _hint.Hide();
        }
        var target = _dragTarget ?? Target();
        if (target is null) return;
        _settings.Edge = target.Value.Edge;
        _settings.Along = target.Value.Along;
        _monitor = target.Value.Monitor;
        var m = _monitor.Bounds;
        (_settings.MonitorX, _settings.MonitorY, _settings.HasMonitor) = (m.Left + m.Width / 2, m.Top + m.Height / 2, true);
        _settings.Save();
        Post(new { type = "edge", edge = _settings.Edge });
        PlaceWindow();
        _hoveredSinceOpen = CursorInside();
    }

    // Which edge the pointer is nearest, on the monitor it's on, and how far along that edge
    // the middle of the mixer is (0 to 1).
    (string Edge, double Along, Native.Monitor Monitor)? Target()
    {
        var (x, y) = Native.CursorPos();
        var m = Native.MonitorAt(x, y);
        var work = m.Work;
        var dist = new Dictionary<string, int> { ["top"] = y - work.Top, ["left"] = x - work.Left, ["right"] = work.Right - x };
        string edge = dist.MinBy(kv => kv.Value).Key;
        var r = Native.WindowRect(_hwnd);
        double cx = (r.Left + r.Right) / 2.0, cy = (r.Top + r.Bottom) / 2.0;
        double along = edge == "top"
            ? Math.Clamp((cx - work.Left) / work.Width, 0, 1)
            : Math.Clamp((cy - work.Top) / work.Height, 0, 1);
        return (edge, along, m);
    }

    void ShowHint()
    {
        _dragTarget = Target();
        if (_dragTarget is not { } t || _hint is null) return;
        var p = PanelRect(t.Edge, t.Along, t.Monitor);
        var work = t.Monitor.Work;
        int a = (int)Math.Round(3 * t.Monitor.Scale), b = (int)Math.Round(7 * t.Monitor.Scale);
        var line = t.Edge switch
        {
            "top" => new Native.PxRect(p.Left, work.Top + a, p.Right, work.Top + b),
            "left" => new Native.PxRect(work.Left + a, p.Top, work.Left + b, p.Bottom),
            _ => new Native.PxRect(work.Right - b, p.Top, work.Right - a, p.Bottom),
        };
        _hint.ShowAt(t.Edge, line, t.Monitor.Scale);
    }

    // Win32 messages that WPF doesn't wrap arrive here.
    IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        switch (msg)
        {
            case Native.WM_HOTKEY:
                if ((int)wParam == HotkeyToggle) Toggle();
                else if ((int)wParam == HotkeyMic) { _audio.SetMicMute(!_audio.Mic.Muted); SendMic(); }
                handled = true;
                break;
            case Native.WM_MOVING when _moving:
                ShowHint();
                break;
            case Native.WM_DISPLAYCHANGE: // a monitor was added, removed or changed resolution
                Dispatcher.BeginInvoke(() =>
                {
                    _monitor = _settings.HasMonitor ? Native.MonitorAt(_settings.MonitorX, _settings.MonitorY) : PrimaryMonitor();
                    PlaceWindow();
                }, DispatcherPriority.Background);
                break;
        }
        return IntPtr.Zero;
    }

    // ---------- tray tooltip ----------

    // The tray tooltip. Before the page has loaded, keep the session list fresh here
    // every few seconds, since the page's refresh timer isn't running yet.
    void UpdateStatus()
    {
        if (!_refreshTimer.IsEnabled && ++_statusTick % 3 == 0) _audio.Refresh();
        string device = _audio.DeviceName;
        int cut = device.IndexOf(" (", StringComparison.Ordinal);
        if (cut > 0) device = device[..cut];
        string status = $"Glass Mixer · {device} {(_audio.MasterMuted ? "muted" : $"{Math.Round(_audio.MasterVolume * 100)}%")}";
        if (status == _status) return;
        _status = status;
        StatusChanged?.Invoke(status);
    }

    // ---------- closing ----------

    public void CloseForQuit()
    {
        _quitting = true;
        Close();
    }

    void OnClosing(object? sender, CancelEventArgs e)
    {
        if (!_quitting) // Alt+F4 only tucks it away; Quit is in the tray menu
        {
            e.Cancel = true;
            Tuck();
            return;
        }
        foreach (var t in new[] { _meterTimer, _refreshTimer, _statusTimer, _probeTimer, _mouseTimer, _fullTimer, _mediaTimer }) t.Stop();
        _hint?.Close();
        Native.UnregisterHotKey(_hwnd, HotkeyToggle);
        Native.UnregisterHotKey(_hwnd, HotkeyMic);
        _audio.Dispose();
    }
}
