using System.Drawing;

namespace Wallflow;

internal sealed class TrayIconService : IDisposable
{
    private readonly System.Windows.Forms.NotifyIcon _icon;
    private bool _hasShownBackgroundNotice;

    public TrayIconService(Action open, Action exit)
    {
        var menu = new System.Windows.Forms.ContextMenuStrip();
        var openItem = new System.Windows.Forms.ToolStripMenuItem("Open Pane");
        openItem.Click += (_, _) => open();
        var exitItem = new System.Windows.Forms.ToolStripMenuItem("Exit Pane");
        exitItem.Click += (_, _) => exit();
        menu.Items.Add(openItem); menu.Items.Add(new System.Windows.Forms.ToolStripSeparator()); menu.Items.Add(exitItem);

        _icon = new System.Windows.Forms.NotifyIcon
        {
            Text = "Pane — per-monitor wallpaper",
            Icon = LoadIcon(),
            ContextMenuStrip = menu,
            Visible = true
        };
        _icon.MouseClick += (_, args) => { if (args.Button == System.Windows.Forms.MouseButtons.Left) open(); };
    }

    public void ShowBackgroundNotice()
    {
        if (_hasShownBackgroundNotice) return;
        _hasShownBackgroundNotice = true;
        _icon.BalloonTipTitle = "Pane is still running";
        _icon.BalloonTipText = "Your per-monitor slideshows will continue in the background.";
        _icon.ShowBalloonTip(2500);
    }

    private static Icon LoadIcon()
    {
        try { return Icon.ExtractAssociatedIcon(Environment.ProcessPath!) ?? SystemIcons.Application; }
        catch { return SystemIcons.Application; }
    }

    public void Dispose()
    {
        _icon.Visible = false; _icon.ContextMenuStrip?.Dispose(); _icon.Dispose();
    }
}
