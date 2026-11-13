using System.Drawing;
using System.Runtime.InteropServices;

namespace GlassMixer.App;

/// <summary>
/// Measures how bright the screen is around the mixer, so the page can add just enough
/// tint to keep its text readable (white text on glass over a white web page isn't).
///
/// It reads four one-pixel lines along the window's outer edge. That edge is part of the
/// window's transparent margin, so those pixels show what is behind the mixer, and reading
/// such thin lines twice a second costs almost nothing.
/// </summary>
static class BackdropProbe
{
    const int SamplesPerSide = 12;

    /// <summary>Average brightness, 0.0 (black) to 1.0 (white), or null if nothing could be read.</summary>
    public static double? Sample(IntPtr hwnd)
    {
        if (!GetWindowRect(hwnd, out var r)) return null;
        int width = r.Right - r.Left, height = r.Bottom - r.Top;
        if (width < 8 || height < 8) return null;

        double sum = 0;
        int count = 0;
        try
        {
            // Top and bottom edges, then left and right edges.
            Read(r.Left, r.Top + 1, width, 1, ref sum, ref count);
            Read(r.Left, r.Bottom - 2, width, 1, ref sum, ref count);
            Read(r.Left + 1, r.Top, 1, height, ref sum, ref count);
            Read(r.Right - 2, r.Top, 1, height, ref sum, ref count);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or ArgumentException or ExternalException)
        {
            return null; // the screen can't be read right now (lock screen, a secure desktop)
        }
        return count == 0 ? null : sum / count;
    }

    static void Read(int x, int y, int width, int height, ref double sum, ref int count)
    {
        // Skip a line that lies outside every monitor (the margin may hang off the screen's edge).
        var middle = new POINT { X = x + width / 2, Y = y + height / 2 };
        if (MonitorFromPoint(middle, MONITOR_DEFAULTTONULL) == IntPtr.Zero) return;

        using var line = new Bitmap(width, height);
        using (var g = Graphics.FromImage(line))
            g.CopyFromScreen(x, y, 0, 0, line.Size);

        int length = Math.Max(width, height);
        for (int i = 0; i < SamplesPerSide; i++)
        {
            int at = (int)((i + 0.5) * length / SamplesPerSide);
            var c = width > height ? line.GetPixel(at, 0) : line.GetPixel(0, at);
            // How bright a colour looks: green counts most, blue least.
            sum += (0.2126 * c.R + 0.7152 * c.G + 0.0722 * c.B) / 255.0;
            count++;
        }
    }

    const int MONITOR_DEFAULTTONULL = 0;

    [StructLayout(LayoutKind.Sequential)]
    struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    struct POINT { public int X, Y; }

    [DllImport("user32.dll")]
    static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);

    [DllImport("user32.dll")]
    static extern IntPtr MonitorFromPoint(POINT point, int flags);
}
