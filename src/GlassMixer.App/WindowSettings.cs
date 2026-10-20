using System.IO;
using System.Text.Json;

namespace GlassMixer.App;

/// <summary>
/// Where the mixer was and how big, saved between runs in %LOCALAPPDATA%\GlassMixer\window.json.
/// The bottom-right corner is stored (not the top-left) because the mixer grows and
/// shrinks around that corner, like a flyout above the tray.
/// </summary>
sealed class WindowSettings
{
    public double Right { get; set; }
    public double Bottom { get; set; }
    public double Width { get; set; }   // the full panel size, even while minimized
    public double Height { get; set; }
    public bool Minimized { get; set; }

    static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GlassMixer", "window.json");

    public static WindowSettings? Load()
    {
        try { return File.Exists(FilePath) ? JsonSerializer.Deserialize<WindowSettings>(File.ReadAllText(FilePath)) : null; }
        catch (Exception ex) when (ex is IOException or JsonException) { return null; }
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this));
        }
        catch (IOException) { /* not critical: next time it opens in the corner */ }
    }
}
