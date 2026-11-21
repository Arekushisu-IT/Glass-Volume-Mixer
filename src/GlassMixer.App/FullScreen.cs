using System.Diagnostics;
using System.Runtime.InteropServices;

namespace GlassMixer.App;

/// <summary>
/// Is a full-screen app in front? Windows itself asks this before showing notifications
/// (Focus assist: "when I'm playing a game"), through SHQueryUserNotificationState.
///
/// Used for two things: the Gaming profile switches on while a game runs, and the pill
/// steps out of the way so it never sits on top of a game or a full-screen video.
/// </summary>
static class FullScreen
{
    public enum Kind { None, Game, Other }

    // Full-screen windows of these are videos or slides, not games.
    static readonly HashSet<string> NotGames = new(StringComparer.OrdinalIgnoreCase)
    {
        "chrome", "msedge", "firefox", "brave", "opera", "vivaldi", "vlc", "mpc-hc64", "mpc-hc", "mpc-be64",
        "PotPlayerMini64", "wmplayer", "Microsoft.Media.Player", "Video.UI", "POWERPNT", "Teams", "ms-teams", "Zoom",
        "explorer", "ApplicationFrameHost",
    };

    public static Kind Check()
    {
        if (SHQueryUserNotificationState(out int state) != 0) return Kind.None;
        switch (state)
        {
            case QUNS_RUNNING_D3D_FULL_SCREEN:
                return Kind.Game; // exclusive full-screen DirectX: always a game
            case QUNS_BUSY:
            case QUNS_PRESENTATION_MODE:
                // Some full-screen window. Borderless games land here too, so look at who owns it.
                GetWindowThreadProcessId(GetForegroundWindow(), out int pid);
                try
                {
                    using var p = Process.GetProcessById(pid);
                    return NotGames.Contains(p.ProcessName) ? Kind.Other : Kind.Game;
                }
                catch (ArgumentException) { return Kind.Other; }
            default:
                return Kind.None;
        }
    }

    const int QUNS_BUSY = 2, QUNS_RUNNING_D3D_FULL_SCREEN = 3, QUNS_PRESENTATION_MODE = 4;

    [DllImport("shell32.dll")]
    static extern int SHQueryUserNotificationState(out int state);

    [DllImport("user32.dll")]
    static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    static extern int GetWindowThreadProcessId(IntPtr hwnd, out int processId);
}
