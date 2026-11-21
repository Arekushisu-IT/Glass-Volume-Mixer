using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace GlassMixer.App;

/// <summary>How the window's see-through background is drawn.</summary>
public enum GlassStyle
{
    /// <summary>No blur at all: windows behind show through sharp, under the page's tint.</summary>
    Clear,
    /// <summary>
    /// Windows' own blur, fixed strength. Only a fallback for Windows versions where
    /// BackdropBlur (the adjustable blur) can't hide the window from screen capture.
    /// </summary>
    Blur,
}

/// <summary>
/// Windows API calls that .NET doesn't wrap for us (P/Invoke).
/// Each [DllImport] line says: "this function lives in that Windows DLL, call it directly".
/// </summary>
static class Native
{
    public const int WM_HOTKEY = 0x0312;
    public const int WM_EXITSIZEMOVE = 0x0232; // a move or resize by the user has finished
    public const int WM_DISPLAYCHANGE = 0x007E; // a monitor was added, removed or changed resolution
    public const uint MOD_ALT = 0x1, MOD_CONTROL = 0x2, MOD_NOREPEAT = 0x4000;

    const int WM_NCLBUTTONDOWN = 0x00A1; // "the mouse went down on the window frame"
    const int HTCAPTION = 2;             // ...on the title bar

    // Which part of the frame a resize starts from. The page sends one of these names.
    static readonly Dictionary<string, int> ResizeEdges = new()
    {
        ["left"] = 10, ["right"] = 11, ["top"] = 12, ["top-left"] = 13,
        ["top-right"] = 14, ["bottom"] = 15, ["bottom-left"] = 16, ["bottom-right"] = 17,
    };

    const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    const int DWMWA_BORDER_COLOR = 34;
    const int DWMWA_CAPTION_COLOR = 35;
    const int DWMWA_COLOR_NONE = unchecked((int)0xFFFFFFFE); // "don't paint this colour at all"
    const int DWMWA_NCRENDERING_POLICY = 2;
    const int DWMNCRP_DISABLED = 1;
    const int DWMWCP_DONOTROUND = 1;
    const int DWM_BB_ENABLE = 1, DWM_BB_BLURREGION = 2;

    // SetWindowCompositionAttribute is undocumented but has been stable since Windows 10.
    // Many apps (TranslucentTB, EarTrumpet) use it for a blur behind a window.
    const int WCA_ACCENT_POLICY = 19;
    const int ACCENT_ENABLE_TRANSPARENTGRADIENT = 2, ACCENT_ENABLE_BLURBEHIND = 3;
    const int ACCENT_FLAG_USE_GRADIENT_COLOR = 2; // use our GradientColor instead of the Windows accent colour

    [StructLayout(LayoutKind.Sequential)]
    struct MARGINS { public int Left, Right, Top, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    struct DWM_BLURBEHIND { public int dwFlags; public bool fEnable; public IntPtr hRgnBlur; public bool fTransitionOnMaximized; }

    [StructLayout(LayoutKind.Sequential)]
    struct AccentPolicy { public int AccentState, AccentFlags; public uint GradientColor; public int AnimationId; }

    [StructLayout(LayoutKind.Sequential)]
    struct WindowCompositionAttributeData { public int Attribute; public IntPtr Data; public int SizeOfData; }

    [StructLayout(LayoutKind.Sequential)]
    struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    struct MONITORINFO { public int cbSize; public RECT rcMonitor, rcWork; public int dwFlags; }

    const int MONITOR_DEFAULTTONEAREST = 2;
    const uint SWP_NOSIZE = 0x1, SWP_NOZORDER = 0x4, SWP_NOACTIVATE = 0x10;

    [DllImport("user32.dll")]
    static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);

    [DllImport("user32.dll")]
    static extern IntPtr MonitorFromWindow(IntPtr hwnd, int flags);

    [DllImport("user32.dll")]
    static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFO info);

    [DllImport("user32.dll")]
    static extern bool SetWindowPos(IntPtr hwnd, IntPtr insertAfter, int x, int y, int cx, int cy, uint flags);

    [DllImport("user32.dll")]
    public static extern bool AllowSetForegroundWindow(int processId);
    public const int ASFW_ANY = -1;

    [DllImport("dwmapi.dll")]
    static extern int DwmExtendFrameIntoClientArea(IntPtr hwnd, ref MARGINS margins);

    [DllImport("dwmapi.dll")]
    static extern int DwmEnableBlurBehindWindow(IntPtr hwnd, ref DWM_BLURBEHIND blurBehind);

    [DllImport("dwmapi.dll")]
    static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    [DllImport("user32.dll")]
    static extern int SetWindowCompositionAttribute(IntPtr hwnd, ref WindowCompositionAttributeData data);

    [DllImport("gdi32.dll")]
    static extern IntPtr CreateRectRgn(int left, int top, int right, int bottom);

    [DllImport("gdi32.dll")]
    static extern bool DeleteObject(IntPtr handle);

    [DllImport("user32.dll")]
    public static extern bool RegisterHotKey(IntPtr hwnd, int id, uint modifiers, uint key);

    [DllImport("user32.dll")]
    public static extern bool UnregisterHotKey(IntPtr hwnd, int id);

    [DllImport("user32.dll")]
    static extern bool ReleaseCapture();

    [DllImport("user32.dll", EntryPoint = "SendMessageW")]
    static extern IntPtr SendMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam);

    /// <summary>
    /// One-time setup. The window is a transparent rectangle and WPF draws the rounded
    /// panel and its shadow inside it, so every Windows-drawn decoration is turned off:
    /// border, title-bar colour, 8px corners and the frame shadow.
    /// </summary>
    public static void PrepareGlassWindow(IntPtr hwnd, bool dark)
    {
        int value = dark ? 1 : 0;
        DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref value, sizeof(int));
        value = DWMWA_COLOR_NONE;
        DwmSetWindowAttribute(hwnd, DWMWA_CAPTION_COLOR, ref value, sizeof(int));
        DwmSetWindowAttribute(hwnd, DWMWA_BORDER_COLOR, ref value, sizeof(int));
        value = DWMWCP_DONOTROUND;
        DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref value, sizeof(int));
        value = DWMNCRP_DISABLED; // no frame shadow around the transparent rectangle
        DwmSetWindowAttribute(hwnd, DWMWA_NCRENDERING_POLICY, ref value, sizeof(int));
    }

    /// <summary>
    /// Makes the window's transparent pixels really transparent, in two steps:
    ///
    /// 1. DwmEnableBlurBehindWindow with an empty region makes Windows respect the
    ///    window's per-pixel transparency, so whatever is behind can show through.
    /// 2. The accent policy decides what Windows draws behind those pixels:
    ///    nothing (Clear), or its own fixed-strength blur (Blur, the fallback).
    ///
    /// The adjustable blur itself is drawn by BackdropBlur on top of a Clear window.
    /// (Extending the frame instead only works with Windows 11's Mica and Acrylic;
    /// without them Windows paints it flat grey, which is what the first version showed.)
    /// </summary>
    public static void SetGlass(IntPtr hwnd, GlassStyle style)
    {
        var noFrame = new MARGINS();
        DwmExtendFrameIntoClientArea(hwnd, ref noFrame);

        IntPtr empty = CreateRectRgn(0, 0, -1, -1);
        var blurBehind = new DWM_BLURBEHIND { dwFlags = DWM_BB_ENABLE | DWM_BB_BLURREGION, fEnable = true, hRgnBlur = empty };
        DwmEnableBlurBehindWindow(hwnd, ref blurBehind);
        DeleteObject(empty);

        SetAccent(hwnd, style == GlassStyle.Clear ? ACCENT_ENABLE_TRANSPARENTGRADIENT : ACCENT_ENABLE_BLURBEHIND, 0x00000000);
    }

    /// <summary>
    /// Slides the window so the panel sits fully inside the nearest monitor's work area
    /// (the screen minus the taskbar). Works in physical pixels, so it's right on monitors
    /// with different scaling, where WPF's Left/Top units don't line up across screens.
    /// </summary>
    /// <param name="insetPx">The transparent margin around the panel, which may hang off the edge.</param>
    public static void KeepOnScreen(IntPtr hwnd, int insetPx)
    {
        if (hwnd == IntPtr.Zero || !GetWindowRect(hwnd, out var r)) return;
        var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (!GetMonitorInfo(MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST), ref info)) return;
        var work = info.rcWork;
        int width = r.Right - r.Left, height = r.Bottom - r.Top;

        // Right/bottom first, then left/top, so a window bigger than the screen keeps its header visible.
        int x = Math.Max(Math.Min(r.Left, work.Right - width + insetPx), work.Left - insetPx);
        int y = Math.Max(Math.Min(r.Top, work.Bottom - height + insetPx), work.Top - insetPx);
        if (x != r.Left || y != r.Top)
            SetWindowPos(hwnd, IntPtr.Zero, x, y, 0, 0, SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);
    }

    static void SetAccent(IntPtr hwnd, int accentState, uint color)
    {
        var accent = new AccentPolicy { AccentState = accentState, AccentFlags = ACCENT_FLAG_USE_GRADIENT_COLOR, GradientColor = color };
        int size = Marshal.SizeOf(accent);
        IntPtr pointer = Marshal.AllocHGlobal(size); // unmanaged memory Windows can read
        try
        {
            Marshal.StructureToPtr(accent, pointer, false);
            var data = new WindowCompositionAttributeData { Attribute = WCA_ACCENT_POLICY, Data = pointer, SizeOfData = size };
            SetWindowCompositionAttribute(hwnd, ref data);
        }
        finally
        {
            Marshal.FreeHGlobal(pointer);
        }
    }

    // ---------- screen edges: monitors, the cursor and placing windows ----------
    // All in physical pixels. WPF's Left/Top are scaled units that don't line up across
    // monitors with different scaling, so the edge placement works below WPF.

    public const int WM_ENTERSIZEMOVE = 0x0231, WM_MOVING = 0x0216;

    /// <summary>A rectangle in physical screen pixels.</summary>
    public readonly record struct PxRect(int Left, int Top, int Right, int Bottom)
    {
        public int Width => Right - Left;
        public int Height => Bottom - Top;
        public bool Contains(int x, int y) => x >= Left && x < Right && y >= Top && y < Bottom;
    }

    /// <summary>A monitor: its whole area, its work area (minus the taskbar) and its scaling (1.0 = 100%).</summary>
    public readonly record struct Monitor(PxRect Bounds, PxRect Work, double Scale);

    [StructLayout(LayoutKind.Sequential)]
    struct POINT { public int X, Y; }

    [DllImport("user32.dll")]
    static extern bool GetCursorPos(out POINT point);

    [DllImport("user32.dll")]
    static extern IntPtr MonitorFromPoint(POINT point, int flags);

    [DllImport("shcore.dll")]
    static extern int GetDpiForMonitor(IntPtr monitor, int dpiType, out uint dpiX, out uint dpiY);

    [DllImport("user32.dll")]
    static extern short GetAsyncKeyState(int key);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    static extern IntPtr SetWindowLongPtr(IntPtr hwnd, int index, IntPtr value);

    public static (int X, int Y) CursorPos() => GetCursorPos(out var p) ? (p.X, p.Y) : (0, 0);

    /// <summary>The monitor at a point, or the nearest one.</summary>
    public static Monitor MonitorAt(int x, int y)
    {
        var handle = MonitorFromPoint(new POINT { X = x, Y = y }, MONITOR_DEFAULTTONEAREST);
        var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        GetMonitorInfo(handle, ref info);
        double scale = GetDpiForMonitor(handle, 0, out uint dpi, out _) == 0 ? dpi / 96.0 : 1.0;
        static PxRect R(RECT r) => new(r.Left, r.Top, r.Right, r.Bottom);
        return new Monitor(R(info.rcMonitor), R(info.rcWork), scale);
    }

    public static PxRect WindowRect(IntPtr hwnd) =>
        GetWindowRect(hwnd, out var r) ? new PxRect(r.Left, r.Top, r.Right, r.Bottom) : default;

    /// <summary>Moves and sizes a window without activating it or changing its place in the stack.</summary>
    public static void Place(IntPtr hwnd, PxRect r) =>
        SetWindowPos(hwnd, IntPtr.Zero, r.Left, r.Top, r.Width, r.Height, SWP_NOZORDER | SWP_NOACTIVATE);

    public static bool LeftButtonDown() => (GetAsyncKeyState(0x01) & 0x8000) != 0;

    /// <summary>
    /// For the drop hint: mouse clicks go straight through it, it never takes focus,
    /// and it stays out of Alt+Tab.
    /// </summary>
    public static void MakeClickThrough(IntPtr hwnd)
    {
        const int GWL_EXSTYLE = -20;
        const long WS_EX_TRANSPARENT = 0x20, WS_EX_TOOLWINDOW = 0x80, WS_EX_LAYERED = 0x80000, WS_EX_NOACTIVATE = 0x8000000;
        long style = GetWindowLongPtr(hwnd, GWL_EXSTYLE).ToInt64();
        SetWindowLongPtr(hwnd, GWL_EXSTYLE, new IntPtr(style | WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW | WS_EX_LAYERED | WS_EX_NOACTIVATE));
    }

    /// <summary>Reads Settings > Personalization > Colors > "Choose your app mode".</summary>
    public static bool IsDarkTheme()
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
        return key?.GetValue("AppsUseLightTheme") is int value && value == 0;
    }

    // These pretend the mouse went down on the title bar (or a frame edge), so Windows
    // runs its own move (or resize) loop. That's how a page with no title bar moves its window.
    public static void BeginSystemMove(IntPtr hwnd)
    {
        ReleaseCapture();
        SendMessage(hwnd, WM_NCLBUTTONDOWN, HTCAPTION, IntPtr.Zero);
    }

    public static void BeginSystemResize(IntPtr hwnd, string edge)
    {
        if (!ResizeEdges.TryGetValue(edge, out int hit)) return;
        ReleaseCapture();
        SendMessage(hwnd, WM_NCLBUTTONDOWN, hit, IntPtr.Zero);
    }
}
