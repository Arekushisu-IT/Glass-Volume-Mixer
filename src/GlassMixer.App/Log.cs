using System.IO;

namespace GlassMixer.App;

/// <summary>
/// A tiny log file for debugging: %LOCALAPPDATA%\GlassMixer\glassmixer.log
/// Desktop apps have no console, so this is where errors from C# and the page end up.
/// If that file can't be written, the reason goes to %TEMP%\GlassMixer-log-error.txt,
/// so a broken log never hides a problem silently.
/// </summary>
static class Log
{
    static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GlassMixer", "glassmixer.log");

    public static void Write(string text)
    {
        string line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}  {text}{Environment.NewLine}";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.AppendAllText(FilePath, line);
        }
        catch (Exception ex)
        {
            // Logging must never crash the app, but say why it failed.
            try { File.AppendAllText(Path.Combine(Path.GetTempPath(), "GlassMixer-log-error.txt"), $"{ex.GetType().Name}: {ex.Message} | {line}"); }
            catch { /* nowhere left to write */ }
        }
    }
}
