using System.IO;
using Microsoft.Win32;

namespace GlassMixer.App;

/// <summary>
/// "Start with Windows": Windows runs every program listed under this registry key
/// when you sign in. HKEY_CURRENT_USER means only for you, and no admin rights needed.
/// </summary>
static class StartWithWindows
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string ValueName = "GlassMixer";

    /// <summary>Passed when Windows starts the app at sign-in, so it waits quietly in the tray.</summary>
    public const string Argument = "--startup";

    public static bool IsEnabled
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(ValueName) is string;
        }
    }

    /// <summary>
    /// When "Start with Windows" is on but its entry points at an .exe that no longer exists
    /// (the app was moved, reinstalled elsewhere or deleted), Windows silently starts nothing
    /// at sign-in. Point the entry at this copy instead.
    /// </summary>
    public static void RepairPath()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
        if (key?.GetValue(ValueName) is not string command) return;
        var path = command.Split('"', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();
        if (path is not null && File.Exists(path)) return;
        key.SetValue(ValueName, $"\"{Environment.ProcessPath}\" {Argument}");
        Log.Write($"Start with Windows pointed at a missing file ({path}); now points here");
    }

    public static void SetEnabled(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (enabled) key.SetValue(ValueName, $"\"{Environment.ProcessPath}\" {Argument}");
        else key.DeleteValue(ValueName, throwOnMissingValue: false);
    }
}
