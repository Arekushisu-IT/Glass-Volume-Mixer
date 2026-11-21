using System.IO;
using System.Text.Json;

namespace GlassMixer.App;

/// <summary>
/// Where the mixer lives, saved between runs in %LOCALAPPDATA%\GlassMixer\window.json:
/// which screen edge ("top", "left" or "right"), how far along it (0 to 1, the middle of
/// the mixer), and a point on the monitor it was on, in physical pixels.
/// </summary>
sealed class WindowSettings
{
    public string Edge { get; set; } = "top";
    public double Along { get; set; } = 0.5;
    public int MonitorX { get; set; }
    public int MonitorY { get; set; }
    public bool HasMonitor { get; set; }

    static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GlassMixer", "window.json");

    public static WindowSettings Load()
    {
        try
        {
            var s = File.Exists(FilePath) ? JsonSerializer.Deserialize<WindowSettings>(File.ReadAllText(FilePath)) : null;
            if (s is null) return new WindowSettings();
            // Files from 1.0 have no edge (the window floated): start at the top.
            if (s.Edge is not ("top" or "left" or "right")) s.Edge = "top";
            s.Along = double.IsFinite(s.Along) ? Math.Clamp(s.Along, 0, 1) : 0.5;
            return s;
        }
        catch (Exception ex) when (ex is IOException or JsonException) { return new WindowSettings(); }
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this));
        }
        catch (IOException) { /* not critical: next time it starts at the top */ }
    }
}
