using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;

namespace GlassMixer.App;

/// <summary>
/// While you drag the mixer: a glowing line on the screen edge where it will land, with a
/// "Release to place it here" label, as in the mockup. A separate small window that the mouse
/// passes straight through, so it never gets in the way of the drag.
/// </summary>
sealed class DockHint : Window
{
    const double Glow = 22;   // room around the line for its glow
    readonly Border _line;
    readonly Border _label;
    IntPtr _hwnd;

    public DockHint()
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;   // a layered window: only the line and label are drawn
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        ResizeMode = ResizeMode.NoResize;

        var accent = Native.IsDarkTheme() ? Color.FromRgb(0x45, 0xCF, 0xC1) : Color.FromRgb(0x0B, 0x85, 0x7B);
        var onAccent = Native.IsDarkTheme() ? Color.FromRgb(0x06, 0x24, 0x21) : Colors.White;
        _line = new Border
        {
            Background = new SolidColorBrush(accent),
            CornerRadius = new CornerRadius(99),
            Effect = new DropShadowEffect { Color = accent, BlurRadius = 18, ShadowDepth = 0, Opacity = 0.9 },
        };
        _label = new Border
        {
            Background = new SolidColorBrush(accent),
            CornerRadius = new CornerRadius(99),
            Padding = new Thickness(10, 5, 10, 5),
            Child = new TextBlock
            {
                Text = "Release to place it here",
                Foreground = new SolidColorBrush(onAccent),
                FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI"),
                FontWeight = FontWeights.SemiBold,
                FontSize = 12,
            },
        };
        var canvas = new Canvas();
        canvas.Children.Add(_line);
        canvas.Children.Add(_label);
        Content = canvas;
        SourceInitialized += (_, _) =>
        {
            _hwnd = new WindowInteropHelper(this).Handle;
            Native.MakeClickThrough(_hwnd);
        };
    }

    /// <summary>Shows the line along <paramref name="line"/> (physical pixels) on the given edge.</summary>
    public void ShowAt(string edge, Native.PxRect line, double scale)
    {
        if (!IsVisible) Show();
        int glow = (int)Math.Round(Glow * scale);
        // The window covers the line plus its glow, and room for the label beside it.
        int labelRoom = (int)Math.Round(190 * scale);
        var area = edge switch
        {
            "top" => new Native.PxRect(line.Left - glow, line.Top - glow, line.Right + glow, line.Bottom + glow + (int)(44 * scale)),
            "left" => new Native.PxRect(line.Left - glow, line.Top - glow, line.Right + glow + labelRoom, line.Bottom + glow),
            _ => new Native.PxRect(line.Left - glow - labelRoom, line.Top - glow, line.Right + glow, line.Bottom + glow),
        };
        Native.Place(_hwnd, area);

        // Inside the window, WPF units: divide pixels by the scale.
        double x = (line.Left - area.Left) / scale, y = (line.Top - area.Top) / scale;
        double w = line.Width / scale, h = line.Height / scale;
        Canvas.SetLeft(_line, x); Canvas.SetTop(_line, y);
        _line.Width = w; _line.Height = h;

        _label.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var size = _label.DesiredSize;
        (double lx, double ly) = edge switch
        {
            "top" => (x + w / 2 - size.Width / 2, y + h + 10),
            "left" => (x + w + 10, y + h / 2 - size.Height / 2),
            _ => (x - 10 - size.Width, y + h / 2 - size.Height / 2),
        };
        Canvas.SetLeft(_label, lx); Canvas.SetTop(_label, ly);
    }
}
