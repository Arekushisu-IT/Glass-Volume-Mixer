using System.Drawing;
using System.Drawing.Imaging;
using System.IO;

namespace GlassMixer.App;

/// <summary>
/// Turns a program's .exe icon into a "data:image/png;base64,..." string the page can
/// use as an &lt;img src&gt;. Each .exe is read once, then remembered.
/// </summary>
static class IconCache
{
    static readonly Dictionary<string, string?> Cache = new(StringComparer.OrdinalIgnoreCase);

    public static string? DataUrl(string? exePath)
    {
        if (exePath is null) return null;
        if (Cache.TryGetValue(exePath, out var cached)) return cached;

        string? url = null;
        try
        {
            using var icon = Icon.ExtractAssociatedIcon(exePath);
            if (icon is not null)
            {
                using var bitmap = icon.ToBitmap();
                using var png = new MemoryStream();
                bitmap.Save(png, ImageFormat.Png);
                url = "data:image/png;base64," + Convert.ToBase64String(png.ToArray());
            }
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
        {
            // No icon: the page falls back to a coloured letter.
        }
        return Cache[exePath] = url;
    }
}
