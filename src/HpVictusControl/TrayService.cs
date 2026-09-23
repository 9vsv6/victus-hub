using System.Drawing;
using System.Windows.Forms;
using HpVictusControl.Bios;

namespace HpVictusControl;

/// <summary>Wraps the WinForms NotifyIcon so MainWindow can stay pure WPF.</summary>
public sealed class TrayService : IDisposable {

    private readonly NotifyIcon _icon;
    private readonly ToolStripMenuItem _maxFanItem;
    private readonly ToolStripMenuItem _balancedItem;
    private readonly ToolStripMenuItem _performanceItem;
    private readonly ToolStripMenuItem _coolItem;

    public event Action? ShowRequested;
    public event Action? ExitRequested;
    public event Action<bool>? MaxFanToggleRequested;
    public event Action<HpFanMode>? FanModeRequested;

    public TrayService() {
        var menu = new ContextMenuStrip { RightToLeft = Loc.IsArabic ? RightToLeft.Yes : RightToLeft.No };

        var showItem = new ToolStripMenuItem(Loc.T("Open Victus Hub"));
        showItem.Click += (_, _) => ShowRequested?.Invoke();
        showItem.Font = new Font(showItem.Font, FontStyle.Bold);
        menu.Items.Add(showItem);
        menu.Items.Add(new ToolStripSeparator());

        _maxFanItem = new ToolStripMenuItem(Loc.T("Max fan speed"));
        _maxFanItem.Click += (_, _) => MaxFanToggleRequested?.Invoke(!_maxFanItem.Checked);
        menu.Items.Add(_maxFanItem);

        var modeMenu = new ToolStripMenuItem(Loc.T("Performance mode"));
        _balancedItem = new ToolStripMenuItem(Loc.T("Balanced"), null, (_, _) => FanModeRequested?.Invoke(HpFanMode.Balanced));
        _performanceItem = new ToolStripMenuItem(Loc.T("Performance"), null, (_, _) => FanModeRequested?.Invoke(HpFanMode.Performance));
        _coolItem = new ToolStripMenuItem(Loc.T("Cool"), null, (_, _) => FanModeRequested?.Invoke(HpFanMode.Cool));
        modeMenu.DropDownItems.Add(_balancedItem);
        modeMenu.DropDownItems.Add(_performanceItem);
        modeMenu.DropDownItems.Add(_coolItem);
        menu.Items.Add(modeMenu);

        menu.Items.Add(new ToolStripSeparator());
        var exitItem = new ToolStripMenuItem(Loc.T("Exit"));
        exitItem.Click += (_, _) => ExitRequested?.Invoke();
        menu.Items.Add(exitItem);

        _icon = new NotifyIcon {
            Icon = LoadAppIcon(),
            Text = "Victus Hub",
            Visible = true,
            ContextMenuStrip = menu,
        };
        _icon.DoubleClick += (_, _) => ShowRequested?.Invoke();
    }

    private static Icon LoadAppIcon() {
        try {
            string? exePath = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(exePath)) {
                Icon? extracted = Icon.ExtractAssociatedIcon(exePath);
                if (extracted != null) return extracted;
            }
        } catch {
            // Fall back below.
        }
        return SystemIcons.Application;
    }

    /// <summary>Re-adds the icon so Explorer re-reads its notification-area settings.</summary>
    public void Refresh() {
        _icon.Visible = false;
        _icon.Visible = true;
    }

    public void SetMaxFanChecked(bool value) => _maxFanItem.Checked = value;

    public void SetActiveMode(HpFanMode mode) {
        _balancedItem.Checked = mode == HpFanMode.Balanced;
        _performanceItem.Checked = mode == HpFanMode.Performance;
        _coolItem.Checked = mode == HpFanMode.Cool;
    }

    public void SetTooltip(string text) {
        // NotifyIcon tooltips are capped at 63 characters.
        _icon.Text = text.Length > 63 ? text[..63] : text;
    }

    public void ShowBalloon(string title, string text) =>
        _icon.ShowBalloonTip(3000, title, text, ToolTipIcon.Info);

    public void ShowWarningBalloon(string title, string text) =>
        _icon.ShowBalloonTip(6000, title, text, ToolTipIcon.Warning);

    public void Dispose() {
        _icon.Visible = false;
        _icon.Dispose();
    }
}
