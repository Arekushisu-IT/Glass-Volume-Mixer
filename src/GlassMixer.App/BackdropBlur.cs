using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace GlassMixer.App;

/// <summary>
/// The adjustable blur behind the mixer.
///
/// Windows' own blur has a fixed strength, so to offer a strength slider we draw the
/// background ourselves, about 30 times a second:
///   1. SetWindowDisplayAffinity(EXCLUDEFROMCAPTURE) tells Windows to leave this window
///      out of every screen capture. It still shows on the monitor as normal.
///   2. BitBlt copies the patch of screen under the window. Because we're left out,
///      the copy shows exactly what's behind the mixer.
///   3. That copy is shown in a WPF Image behind the web page, through WPF's
///      BlurEffect, which runs on the graphics card at the chosen radius.
///
/// Side effect: while this runs, the mixer doesn't appear in screenshots or screen sharing.
/// </summary>
sealed class BackdropBlur : IDisposable
{
    readonly Window _window;
    readonly Image _image;
    readonly BlurEffect _effect = new() { KernelType = KernelType.Gaussian, RenderingBias = RenderingBias.Performance };
    readonly DispatcherTimer _timer = new(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(33) };

    IntPtr _hwnd, _memoryDc, _dib, _bits;
    WriteableBitmap? _bitmap;
    int _width, _height;
    double _radius;

    public BackdropBlur(Window window, Image image)
    {
        _window = window;
        _image = image;
        _image.Effect = _effect;
        _timer.Tick += (_, _) => Capture();
    }

    public bool IsRunning => _timer.IsEnabled;

    /// <summary>Starts drawing the blur. False on Windows versions without capture exclusion (before Windows 10 2004).</summary>
    public bool Start(IntPtr hwnd, double radius)
    {
        _hwnd = hwnd;
        if (!SetWindowDisplayAffinity(hwnd, WDA_EXCLUDEFROMCAPTURE)) return false;
        SetRadius(radius);
        _image.Visibility = Visibility.Visible;
        Capture();
        _timer.Start();
        return true;
    }

    public void SetRadius(double radius)
    {
        _radius = radius;
        _effect.Radius = radius;
    }

    public void Stop()
    {
        _timer.Stop();
        _image.Visibility = Visibility.Collapsed;
        if (_hwnd != IntPtr.Zero) SetWindowDisplayAffinity(_hwnd, WDA_NONE);
    }

    void Capture()
    {
        if (!_window.IsVisible || !GetWindowRect(_hwnd, out var r)) return;

        double scale = VisualTreeHelper.GetDpi(_window).DpiScaleX;
        // Copy a bit more than the window: near the edges the blur pulls in pixels from
        // up to one radius away, and without them the edges would fade to black.
        int pad = (int)Math.Ceiling(_radius * scale) + 2;
        int w = r.Right - r.Left + pad * 2, h = r.Bottom - r.Top + pad * 2;
        if (w <= 0 || h <= 0) return;
        EnsureBuffers(w, h);

        IntPtr screen = GetDC(IntPtr.Zero);
        try { BitBlt(_memoryDc, 0, 0, w, h, screen, r.Left - pad, r.Top - pad, SRCCOPY); }
        finally { ReleaseDC(IntPtr.Zero, screen); }
        GdiFlush(); // make sure the copy has finished before reading the pixels

        _bitmap!.WritePixels(new Int32Rect(0, 0, w, h), _bits, w * h * 4, w * 4);
        _image.Margin = new Thickness(-pad / scale, -pad / scale, 0, 0);
        _image.Width = w / scale;
        _image.Height = h / scale;
    }

    // A DIB section is a bitmap whose pixels live in memory we can read directly.
    // It's made once and reused until the window changes size.
    void EnsureBuffers(int w, int h)
    {
        if (_bitmap is not null && w == _width && h == _height) return;
        FreeBuffers();

        var header = new BITMAPINFOHEADER
        {
            biSize = Marshal.SizeOf<BITMAPINFOHEADER>(),
            biWidth = w,
            biHeight = -h, // negative: rows run top to bottom, like WPF expects
            biPlanes = 1,
            biBitCount = 32,
        };
        IntPtr screen = GetDC(IntPtr.Zero);
        _memoryDc = CreateCompatibleDC(screen);
        ReleaseDC(IntPtr.Zero, screen);
        _dib = CreateDIBSection(_memoryDc, ref header, 0, out _bits, IntPtr.Zero, 0);
        SelectObject(_memoryDc, _dib);

        _bitmap = new WriteableBitmap(w, h, 96, 96, PixelFormats.Bgr32, null);
        _image.Source = _bitmap;
        (_width, _height) = (w, h);
    }

    void FreeBuffers()
    {
        if (_dib != IntPtr.Zero) DeleteObject(_dib);
        if (_memoryDc != IntPtr.Zero) DeleteDC(_memoryDc);
        (_dib, _memoryDc, _bits, _bitmap) = (IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, null);
    }

    public void Dispose()
    {
        Stop();
        FreeBuffers();
    }

    // ----- Windows API -----

    const uint WDA_NONE = 0x0, WDA_EXCLUDEFROMCAPTURE = 0x11;
    const int SRCCOPY = 0x00CC0020;

    [StructLayout(LayoutKind.Sequential)]
    struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    struct BITMAPINFOHEADER
    {
        public int biSize, biWidth, biHeight;
        public short biPlanes, biBitCount;
        public int biCompression, biSizeImage, biXPelsPerMeter, biYPelsPerMeter, biClrUsed, biClrImportant;
    }

    [DllImport("user32.dll")] static extern bool SetWindowDisplayAffinity(IntPtr hwnd, uint affinity);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
    [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr hwnd);
    [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr hwnd, IntPtr hdc);
    [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleDC(IntPtr hdc);
    [DllImport("gdi32.dll")] static extern bool DeleteDC(IntPtr hdc);
    [DllImport("gdi32.dll")] static extern IntPtr CreateDIBSection(IntPtr hdc, ref BITMAPINFOHEADER header, uint usage, out IntPtr bits, IntPtr section, uint offset);
    [DllImport("gdi32.dll")] static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);
    [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr obj);
    [DllImport("gdi32.dll")] static extern bool BitBlt(IntPtr dest, int x, int y, int w, int h, IntPtr src, int sx, int sy, int rop);
    [DllImport("gdi32.dll")] static extern bool GdiFlush();
}
