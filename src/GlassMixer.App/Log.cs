using System.IO;

namespace GlassMixer.App;

/// <summary>
/// A tiny log file for debugging: %LOCALAPPDATA%\GlassMixer\glassmixer.log
/// Desktop apps have no console, so this is where errors from C# and the page end up.
/// </summary>
static class Log
{
    static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GlassMixer", "glassmixer.log");

    static Log() => Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);

    public static void Write(string text)
    {
        try { File.AppendAllText(FilePath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}  {text}{Environment.NewLine}"); }
        catch (IOException) { /* logging must never crash the app */ }
    }
}
