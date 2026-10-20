using System.Drawing;
using System.IO;
using Forms = System.Windows.Forms;

namespace GlassMixer.App;

/// <summary>
/// The Glass Mixer icon in the notification area (system tray). Left-click shows or hides
/// the mixer; the tooltip shows the output device and volume; right-click opens a menu.
/// </summary>
sealed class TrayIcon : IDisposable
{
    readonly Forms.NotifyIcon _icon;
    readonly Icon _image;

    public TrayIcon(Action onToggle, Action onQuit)
    {
        // app.ico holds several sizes; ask for the one the tray uses at this display scaling.
        _image = new Icon(Path.Combine(AppContext.BaseDirectory, "app.ico"), Forms.SystemInformation.SmallIconSize);
        _icon = new Forms.NotifyIcon { Icon = _image, Text = "Glass Mixer", Visible = true };
        _icon.MouseClick += (_, e) => { if (e.Button == Forms.MouseButtons.Left) onToggle(); };

        var startWithWindows = new Forms.ToolStripMenuItem("Start with Windows") { CheckOnClick = true, Checked = StartWithWindows.IsEnabled };
        startWithWindows.CheckedChanged += (_, _) => StartWithWindows.SetEnabled(startWithWindows.Checked);

        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Show or hide   Ctrl+Alt+V", null, (_, _) => onToggle());
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add(startWithWindows);
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Quit Glass Mixer", null, (_, _) => onQuit());
        _icon.ContextMenuStrip = menu;
    }

    /// <summary>
    /// The first time the mixer is closed, a Windows notification explains that it's
    /// still running and how to open it again. A small marker file remembers it was shown.
    /// </summary>
    public void ShowStillRunningHintOnce()
    {
        var marker = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GlassMixer", "hint-shown");
        if (File.Exists(marker)) return;
        _icon.ShowBalloonTip(6000, "Glass Mixer is still running",
            "Open it again from this tray icon, with Ctrl+Alt+V, or from the Start menu. To close it completely, right-click the icon and choose Quit.",
            Forms.ToolTipIcon.Info);
        try { File.WriteAllText(marker, DateTime.Now.ToString("O")); } catch (IOException) { }
    }

    /// <summary>Updates the hover text, e.g. "Glass Mixer · Headphones 66%". Windows allows 127 characters.</summary>
    public void SetTooltip(string text)
    {
        text = text.Length > 127 ? text[..126] + "…" : text;
        if (_icon.Text != text) _icon.Text = text;
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
        _image.Dispose();
    }
}
