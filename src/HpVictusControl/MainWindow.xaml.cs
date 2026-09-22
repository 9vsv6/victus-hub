using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using HpVictusControl.Bios;
using HpVictusControl.Games;
using HpVictusControl.Updates;
using Border = System.Windows.Controls.Border;
using Brush = System.Windows.Media.Brush;
using Button = System.Windows.Controls.Button;
using CheckBox = System.Windows.Controls.CheckBox;
using RadioButton = System.Windows.Controls.RadioButton;
using Color = System.Windows.Media.Color;
using MessageBox = System.Windows.MessageBox;
using Orientation = System.Windows.Controls.Orientation;
using SolidColorBrush = System.Windows.Media.SolidColorBrush;
using TextBox = System.Windows.Controls.TextBox;
using Point = System.Windows.Point;
using Size = System.Windows.Size;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;

namespace HpVictusControl;

public partial class MainWindow : Window {

    private readonly HpBiosClient _bios = new();
    private readonly TrayService _tray = new();
    private readonly DispatcherTimer _pollTimer = new() { Interval = TimeSpan.FromSeconds(2) };

    private readonly AppSettings _settings = AppSettings.Load();

    private bool _initializing = true;
    private bool _exitRequested;
    private HpFanMode _currentMode = HpFanMode.Balanced;

    public MainWindow() {
        InitializeComponent();

        if (_settings.LastTab == "Drivers") DriversTabRadio.IsChecked = true;
        else if (_settings.LastTab == "System") SystemTabRadio.IsChecked = true;
        else if (_settings.LastTab == "Settings") SettingsTabRadio.IsChecked = true;
        if (_settings.LastSection == "Games") GamesSectionRadio.IsChecked = true;

        RestoreWindowBounds();

        ApplyTheme(_settings.DarkTheme);
        DarkThemeCheckBox.IsChecked = _settings.DarkTheme;
        MatchWindowsThemeCheckBox.IsChecked = _settings.MatchWindowsTheme;
        TuneWifiCheckBox.IsChecked = _settings.TuneWifiForGames;
        AutoCleanDownloadsCheckBox.IsChecked = _settings.AutoCleanDriverDownloads;
        Microsoft.Win32.SystemEvents.UserPreferenceChanged += SystemEvents_UserPreferenceChanged;
        TempAlertCheckBox.IsChecked = _settings.TempAlertsEnabled;
        TempAlertSlider.Value = _settings.TempAlertThreshold;
        ExitOnCloseCheckBox.IsChecked = _settings.ExitOnClose;
        AlwaysOnTopCheckBox.IsChecked = _settings.AlwaysOnTop;

        SpeakerAwakeCard.Visibility = SpeakerPowerSettings.IsSupported ? Visibility.Visible : Visibility.Collapsed;
        KeepSpeakersAwakeCheckBox.IsChecked = SpeakerPowerSettings.IsKeptAwake();        AppVersionText.Text = $"Version {System.Reflection.Assembly.GetExecutingAssembly().GetName().Version}";

        _tray.ShowRequested += () => Dispatcher.Invoke(() => {
            ShowInTaskbar = true;
            Show();
            WindowState = WindowState.Normal;
            Activate();
        });
        _tray.ExitRequested += () => Dispatcher.Invoke(ExitApplication);
        _tray.MaxFanToggleRequested += enabled => Dispatcher.Invoke(() => MaxFanCheckBox.IsChecked = enabled);
        _tray.FanModeRequested += mode => Dispatcher.Invoke(() => SetActiveModeRadio(mode));

        _pollTimer.Tick += (_, _) => RefreshStats();

        // Nothing's watching the live stats while hidden in the tray, so poll far less often —
        // temperature alerts and game-profile switching still run, just less frequently.
        IsVisibleChanged += (_, e) => _pollTimer.Interval = TimeSpan.FromSeconds((bool)e.NewValue ? 2 : 15);

        Microsoft.Win32.SystemEvents.PowerModeChanged += SystemEvents_PowerModeChanged;

        Loaded += MainWindow_Loaded;
        Closing += MainWindow_Closing;
    }

    protected override void OnSourceInitialized(EventArgs e) {
        base.OnSourceInitialized(e);
        // Without this, DWM keeps drawing the native light-mode title bar/frame even
        // though our content is dark, which shows up as a mismatched pale seam at the
        // very top of the window.
        ApplyTitleBarTheme(_settings.DarkTheme);
        RegisterCycleModeHotkey();
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    private const int DwmwaUseImmersiveDarkMode = 20;

    private void ApplyTitleBarTheme(bool dark) {
        IntPtr hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;
        int useDark = dark ? 1 : 0;
        DwmSetWindowAttribute(hwnd, DwmwaUseImmersiveDarkMode, ref useDark, sizeof(int));
    }

    // ----- Global hotkeys: cycle performance mode and toggle max fan from anywhere -----------

    [DllImport("user32.dll")]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll")]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    private const int CycleModeHotkeyId = 0x4859;
    private const int MaxFanHotkeyId = 0x485A;
    private const int WmHotkey = 0x0312;

    private bool _hotkeyHookAdded;

    private (int Id, string Name, string Setting)[] HotkeyDefinitions() => new[] {
        (CycleModeHotkeyId, "cycle performance mode", _settings.CycleModeHotkey),
        (MaxFanHotkeyId, "toggle max fan", _settings.MaxFanHotkey),
    };

    private void RegisterCycleModeHotkey() {
        HashSet<int> failed = RegisterHotkeys();
        if (failed.Count == 0) return;

        IEnumerable<string> taken = HotkeyDefinitions().Where(d => failed.Contains(d.Id)).Select(d => $"{d.Setting} ({d.Name})");
        ShowHotkeyStatus($"Another app already uses {string.Join(" and ", taken)}. Pick a different shortcut below.");
    }

    /// <summary>(Re)registers every configured shortcut and returns the ids Windows refused.</summary>
    private HashSet<int> RegisterHotkeys() {
        var failed = new HashSet<int>();
        IntPtr hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd != IntPtr.Zero) {
            if (!_hotkeyHookAdded) {
                HwndSource.FromHwnd(hwnd)?.AddHook(HotkeyWndProc);
                _hotkeyHookAdded = true;
            }

            UnregisterHotkeys();
            foreach ((int id, _, string setting) in HotkeyDefinitions()) {
                if (Hotkey.TryParse(setting, out Hotkey hotkey) && !RegisterHotKey(hwnd, id, hotkey.NativeModifiers, hotkey.VirtualKey))
                    failed.Add(id);
            }
        }

        RefreshHotkeyDisplay();
        return failed;
    }

    private void UnregisterHotkeys() {
        IntPtr hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;
        UnregisterHotKey(hwnd, CycleModeHotkeyId);
        UnregisterHotKey(hwnd, MaxFanHotkeyId);
    }

    private IntPtr HotkeyWndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled) {
        if (msg != WmHotkey) return IntPtr.Zero;

        switch (wParam.ToInt32()) {
            case CycleModeHotkeyId: CycleModeViaHotkey(); handled = true; break;
            case MaxFanHotkeyId: ToggleMaxFanViaHotkey(); handled = true; break;
        }
        return IntPtr.Zero;
    }

    private void ToggleMaxFanViaHotkey() {
        if (!_bios.IsAvailable || !MaxFanCheckBox.IsEnabled) return;

        // The checkbox's own handler talks to the BIOS and keeps the tray menu in step.
        bool enable = MaxFanCheckBox.IsChecked != true;
        MaxFanCheckBox.IsChecked = enable;
        _tray.ShowBalloon("HP Victus Control", enable ? "Max fan on" : "Max fan off");
    }

    private void RefreshHotkeyDisplay() {
        bool cycleSet = Hotkey.TryParse(_settings.CycleModeHotkey, out Hotkey cycle);
        bool maxFanSet = Hotkey.TryParse(_settings.MaxFanHotkey, out Hotkey maxFan);

        if (_recordingHotkey != "CycleMode") CycleModeHotkeyButton.Content = cycleSet ? cycle.ToString() : "Off";
        if (_recordingHotkey != "MaxFan") MaxFanHotkeyButton.Content = maxFanSet ? maxFan.ToString() : "Off";

        CycleModeTipText.Text = $"Tip: press {cycle} anywhere to cycle performance mode";
        CycleModeTipText.Visibility = cycleSet ? Visibility.Visible : Visibility.Collapsed;
        MaxFanCheckBox.ToolTip = maxFanSet ? $"Shortcut: {maxFan}" : null;
    }

    // ----- Recording a new shortcut in Settings -----

    // Which shortcut is waiting for keys ("CycleMode" / "MaxFan"), or null when none is.
    private string? _recordingHotkey;

    private void HotkeyButton_Click(object sender, RoutedEventArgs e) {
        if (sender is not Button { Tag: string which } button) return;
        if (_recordingHotkey == which) {
            StopRecordingHotkey();
            return;
        }

        StopRecordingHotkey();
        _recordingHotkey = which;
        // Registered shortcuts are swallowed before any window sees them, so release them while
        // recording — otherwise pressing the current combo would just fire it.
        UnregisterHotkeys();
        button.Content = "Press keys…";
        HotkeyStatusText.Visibility = Visibility.Collapsed;
        Keyboard.Focus(button);
    }

    private void HotkeyButton_PreviewKeyDown(object sender, KeyEventArgs e) {
        if (sender is not Button { Tag: string which } button || _recordingHotkey != which) return;
        e.Handled = true;

        Key key = e.Key switch { Key.System => e.SystemKey, Key.ImeProcessed => e.ImeProcessedKey, _ => e.Key };
        ModifierKeys modifiers = Keyboard.Modifiers;

        if (Hotkey.IsModifierKey(key)) {
            button.Content = Hotkey.DescribePartial(modifiers);
            return;
        }
        if (modifiers == ModifierKeys.None && key == Key.Escape) {
            StopRecordingHotkey();
            return;
        }
        if (modifiers == ModifierKeys.None && key is Key.Back or Key.Delete) {
            SaveHotkey(which, "");
            StopRecordingHotkey();
            ShowHotkeyStatus("Shortcut turned off.");
            return;
        }

        var hotkey = new Hotkey(modifiers, key);
        if (!hotkey.HasRequiredModifier) {
            ShowHotkeyStatus($"{hotkey} would block that key everywhere — hold Ctrl, Alt or Win as well.");
            button.Content = "Press keys…";
            return;
        }

        string other = which == "MaxFan" ? _settings.CycleModeHotkey : _settings.MaxFanHotkey;
        if (Hotkey.TryParse(other, out Hotkey otherHotkey) && otherHotkey == hotkey) {
            ShowHotkeyStatus($"{hotkey} is already the other shortcut.");
            button.Content = "Press keys…";
            return;
        }

        string previous = which == "MaxFan" ? _settings.MaxFanHotkey : _settings.CycleModeHotkey;
        SaveHotkey(which, hotkey.ToString());
        _recordingHotkey = null;

        int id = which == "MaxFan" ? MaxFanHotkeyId : CycleModeHotkeyId;
        if (RegisterHotkeys().Contains(id)) {
            // Windows (or another app) owns that combination; keep the one that worked.
            SaveHotkey(which, previous);
            RegisterHotkeys();
            ShowHotkeyStatus($"{hotkey} is already taken by Windows or another app. Try a different combination.");
            return;
        }
        ShowHotkeyStatus($"Saved: {hotkey}");
    }

    // Keeps the "Ctrl+Alt+…" preview honest when a modifier is let go before the key is pressed.
    private void HotkeyButton_PreviewKeyUp(object sender, KeyEventArgs e) {
        if (sender is not Button { Tag: string which } button || _recordingHotkey != which) return;
        e.Handled = true;
        button.Content = Keyboard.Modifiers == ModifierKeys.None ? "Press keys…" : Hotkey.DescribePartial(Keyboard.Modifiers);
    }

    private void HotkeyButton_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e) {
        if (sender is Button { Tag: string which } && _recordingHotkey == which) StopRecordingHotkey();
    }

    private void StopRecordingHotkey() {
        if (_recordingHotkey == null) return;
        _recordingHotkey = null;
        RegisterHotkeys();
    }

    private void SaveHotkey(string which, string value) {
        if (which == "MaxFan") _settings.MaxFanHotkey = value;
        else _settings.CycleModeHotkey = value;
        _settings.Save();
    }

    private void ShowHotkeyStatus(string text) {
        HotkeyStatusText.Text = text;
        HotkeyStatusText.Visibility = Visibility.Visible;
    }

    private void CycleModeViaHotkey() {
        HpFanMode next = _currentMode switch {
            HpFanMode.Balanced => HpFanMode.Performance,
            HpFanMode.Performance => HpFanMode.Cool,
            _ => HpFanMode.Balanced
        };
        SetActiveModeRadio(next);
        _tray.ShowBalloon("HP Victus Control", $"Switched to {next} mode");
    }

    private void RestoreWindowBounds() {
        if (_settings.WindowWidth <= 0 || _settings.WindowHeight <= 0) return;

        // Guard against restoring to a monitor that's since been unplugged/undocked, which
        // would otherwise put the window somewhere the user can't see or reach it.
        double vLeft = SystemParameters.VirtualScreenLeft;
        double vTop = SystemParameters.VirtualScreenTop;
        double vRight = vLeft + SystemParameters.VirtualScreenWidth;
        double vBottom = vTop + SystemParameters.VirtualScreenHeight;
        bool onScreen = _settings.WindowLeft + 50 < vRight
            && _settings.WindowLeft + _settings.WindowWidth - 50 > vLeft
            && _settings.WindowTop + 50 < vBottom
            && _settings.WindowTop + _settings.WindowHeight - 50 > vTop;
        if (!onScreen) return;

        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = _settings.WindowLeft;
        Top = _settings.WindowTop;
        Width = Math.Max(MinWidth, _settings.WindowWidth);
        Height = Math.Max(MinHeight, _settings.WindowHeight);
    }

    private void SaveWindowBounds() {
        if (WindowState != WindowState.Normal) return;
        _settings.WindowLeft = Left;
        _settings.WindowTop = Top;
        _settings.WindowWidth = Width;
        _settings.WindowHeight = Height;
        _settings.Save();
    }

    private void TopTab_Changed(object sender, RoutedEventArgs e) {
        // PerformanceTabRadio has IsChecked="True" in XAML, which fires this Checked event
        // during InitializeComponent() itself — before the later-declared tab panels (deep
        // inside the ScrollViewer) have been constructed and assigned to their fields yet.
        if (PerformanceTabPanel == null) return;

        PerformanceTabPanel.Visibility = PerformanceTabRadio.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        DriversTabPanel.Visibility = DriversTabRadio.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        SystemTabPanel.Visibility = SystemTabRadio.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        SettingsTabPanel.Visibility = SettingsTabRadio.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;

        if (SystemTabRadio.IsChecked == true && !_initializing) LoadSystemTab();

        if (_initializing) return;
        _settings.LastTab = DriversTabRadio.IsChecked == true ? "Drivers"
            : SystemTabRadio.IsChecked == true ? "System"
            : SettingsTabRadio.IsChecked == true ? "Settings" : "Performance";
        _settings.Save();
    }

    private void PerformanceSection_Changed(object sender, RoutedEventArgs e) {
        // Same InitializeComponent() ordering issue as TopTab_Changed: the IsChecked="True" radio
        // fires before the section panels further down the XAML exist.
        if (PerformanceSectionPanel == null || GamesSectionPanel == null) return;

        bool games = GamesSectionRadio.IsChecked == true;
        PerformanceSectionPanel.Visibility = games ? Visibility.Collapsed : Visibility.Visible;
        GamesSectionPanel.Visibility = games ? Visibility.Visible : Visibility.Collapsed;

        if (_initializing) return;
        _settings.LastSection = games ? "Games" : "Performance";
        _settings.Save();
    }

    // ----- Temperature dials -----

    // The dial is a half circle from 14,76 to 106,76 (radius 46) drawn over 0–100°C, the range the
    // CPU actually works in: 100°C is where this chip starts slowing itself down to cool off.
    private const double GaugeMaxCelsius = 100;

    private void UpdateTemperatureGauge(System.Windows.Shapes.Path arc, double? celsius) {
        if (celsius is not double value || value <= 0) {
            arc.Data = null;
            return;
        }

        double fraction = Math.Clamp(value / GaugeMaxCelsius, 0, 1);
        const double centreX = 60, centreY = 76, radius = 46;
        double angle = Math.PI * (1 - fraction); // π (left) → 0 (right)
        var end = new Point(centreX + radius * Math.Cos(angle), centreY - radius * Math.Sin(angle));

        var figure = new PathFigure { StartPoint = new Point(centreX - radius, centreY) };
        figure.Segments.Add(new ArcSegment(end, new Size(radius, radius), 0, fraction > 0.5,
            SweepDirection.Clockwise, isStroked: true));
        arc.Data = new PathGeometry(new[] { figure });

        // Green while there's headroom, amber once it's hot, red at the point it throttles.
        string brushKey = value >= 95 ? "GaugeHotBrush" : value >= 85 ? "GaugeWarmBrush" : "AccentBrush";
        arc.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, brushKey);
    }

    // The strip beside the section tabs, so temperatures and the active mode stay in view
    // while you're on the Games list.
    private void UpdateSectionStatus() {
        SectionStatusText.Text = $"CPU {TemperatureText.Text}  ·  GPU {GpuTempText.Text}  ·  {_currentMode}";
    }

    // ----- Match Windows' theme -----

    // Windows keeps the apps theme in AppsUseLightTheme (0 = dark); null if it can't be read.
    private static bool? WindowsUsesDarkTheme() {
        try {
            using Microsoft.Win32.RegistryKey? key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int light ? light == 0 : null;
        } catch {
            return null;
        }
    }

    private void MatchWindowsThemeCheckBox_Changed(object sender, RoutedEventArgs e) {
        bool match = MatchWindowsThemeCheckBox.IsChecked == true;
        DarkThemeCheckBox.IsEnabled = !match;
        if (match) FollowWindowsTheme();

        if (_initializing) return;
        _settings.MatchWindowsTheme = match;
        _settings.Save();
    }

    private void FollowWindowsTheme() {
        // Going through the checkbox keeps one code path for applying and saving the theme.
        if (WindowsUsesDarkTheme() is bool dark && DarkThemeCheckBox.IsChecked != dark) DarkThemeCheckBox.IsChecked = dark;
    }

    // Windows announces a light/dark switch as a "General" preference change.
    private void SystemEvents_UserPreferenceChanged(object sender, Microsoft.Win32.UserPreferenceChangedEventArgs e) {
        if (e.Category != Microsoft.Win32.UserPreferenceCategory.General) return;
        Dispatcher.BeginInvoke(() => {
            if (MatchWindowsThemeCheckBox.IsChecked == true) FollowWindowsTheme();
        });
    }

    private void DarkThemeCheckBox_Changed(object sender, RoutedEventArgs e) {
        bool dark = DarkThemeCheckBox.IsChecked == true;
        ApplyTheme(dark);

        if (_initializing) return;
        _settings.DarkTheme = dark;
        _settings.Save();

        // Type tags carry their own light/dark colours rather than theme resources.
        if (_lastUpdates.Count > 0) RenderUpdateRows(_lastUpdates);
    }

    private void StartWithWindowsCheckBox_Changed(object sender, RoutedEventArgs e) {
        bool enabled = StartWithWindowsCheckBox.IsChecked == true;

        if (!_initializing) {
            try {
                StartupManager.SetEnabled(enabled);
            } catch (Exception ex) {
                MessageBox.Show(this, $"Couldn't update startup setting: {ex.Message}", "HP Victus Control", MessageBoxButton.OK, MessageBoxImage.Warning);
                StartWithWindowsCheckBox.IsChecked = !enabled;
                return;
            }
        }

        _settings.StartWithWindows = enabled;
        _settings.Save();
    }

    private bool _revertingSpeakerToggle;

    private void KeepSpeakersAwakeCheckBox_Changed(object sender, RoutedEventArgs e) {
        if (_initializing || _revertingSpeakerToggle) return;

        bool keepAwake = KeepSpeakersAwakeCheckBox.IsChecked == true;
        try {
            if (keepAwake) {
                int? previous = SpeakerPowerSettings.GetIdleSeconds();
                if (previous > 0) {
                    _settings.SpeakerIdleSecondsBeforeKeepAwake = previous;
                    _settings.Save();
                }
                SpeakerPowerSettings.SetIdleSeconds(0);
            } else {
                // 5 seconds is the Realtek driver's default when no earlier value was saved.
                SpeakerPowerSettings.SetIdleSeconds(_settings.SpeakerIdleSecondsBeforeKeepAwake ?? 5);
            }
            SpeakerAwakeStatusText.Text = "Restart Windows to apply this.";
            SpeakerAwakeStatusText.Visibility = Visibility.Visible;
        } catch (Exception ex) {
            MessageBox.Show(this, $"Couldn't change the speaker setting: {ex.Message}", "HP Victus Control",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            _revertingSpeakerToggle = true;
            KeepSpeakersAwakeCheckBox.IsChecked = !keepAwake;
            _revertingSpeakerToggle = false;
        }
    }

    private void AlwaysOnTopCheckBox_Changed(object sender, RoutedEventArgs e) {
        bool onTop = AlwaysOnTopCheckBox.IsChecked == true;
        Topmost = onTop;

        if (_initializing) return;
        _settings.AlwaysOnTop = onTop;
        _settings.Save();
    }

    private void ExitOnCloseCheckBox_Changed(object sender, RoutedEventArgs e) {
        if (_initializing) return;
        _settings.ExitOnClose = ExitOnCloseCheckBox.IsChecked == true;
        _settings.Save();
    }

    private void TempAlertCheckBox_Changed(object sender, RoutedEventArgs e) {
        if (_initializing) return;
        _settings.TempAlertsEnabled = TempAlertCheckBox.IsChecked == true;
        _settings.Save();
    }

    private void TempAlertSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e) {
        // Value gets coerced into [60,100] as soon as XAML sets Minimum/Maximum on this
        // slider (default Value 0 is out of range), firing this before InitializeComponent
        // has finished wiring up later-declared sibling elements.
        if (TempAlertSliderText == null) return;

        TempAlertSliderText.Text = $"{(int)TempAlertSlider.Value}°C";
        if (_initializing) return;
        _settings.TempAlertThreshold = TempAlertSlider.Value;
        _settings.Save();
    }

    // Fires once when a reading crosses the threshold, then waits for it to drop 5° below
    // before it can fire again — avoids re-notifying every 2-second poll tick while it's hot.
    private bool _cpuTempAlertActive;
    private bool _gpuTempAlertActive;

    private void CheckTemperatureAlert(string label, double? celsius, ref bool alertActive) {
        if (TempAlertCheckBox.IsChecked != true || !celsius.HasValue) return;

        double threshold = _settings.TempAlertThreshold;
        if (!alertActive && celsius.Value >= threshold) {
            alertActive = true;
            _tray.ShowWarningBalloon("HP Victus Control", $"{label} temperature is high: {celsius.Value:0.#}°C");
        } else if (alertActive && celsius.Value <= threshold - 5) {
            alertActive = false;
        }
    }

    private void ApplyTheme(bool dark) {
        (string Key, Color Light, Color Dark)[] palette = {
            ("AccentBrush", Color.FromRgb(0x22, 0xC5, 0x5E), Color.FromRgb(0x22, 0xC5, 0x5E)),
            ("AccentHoverBrush", Color.FromRgb(0x1C, 0xA8, 0x4E), Color.FromRgb(0x16, 0xA3, 0x4A)),
            ("CardBrush", Color.FromRgb(0xFF, 0xFF, 0xFF), Color.FromRgb(0x15, 0x17, 0x1A)),
            ("BorderBrush2", Color.FromRgb(0xE4, 0xE6, 0xEB), Color.FromRgb(0x26, 0x2B, 0x31)),
            ("TrackBrush", Color.FromRgb(0xEE, 0xF0, 0xF3), Color.FromRgb(0x1D, 0x21, 0x25)),
            ("TextPrimaryBrush", Color.FromRgb(0x1B, 0x1F, 0x24), Color.FromRgb(0xF2, 0xF3, 0xF5)),
            ("TextSecondaryBrush", Color.FromRgb(0x8A, 0x8F, 0x98), Color.FromRgb(0x9A, 0xA0, 0xAA)),
            ("GlowFillBrush", Color.FromRgb(0xE8, 0xF9, 0xEF), Color.FromRgb(0x1B, 0x33, 0x23)),
            ("GaugeWarmBrush", Color.FromRgb(0xD9, 0x77, 0x06), Color.FromRgb(0xEF, 0x9F, 0x27)),
            ("GaugeHotBrush", Color.FromRgb(0xDC, 0x26, 0x26), Color.FromRgb(0xE2, 0x4B, 0x4A)),
        };

        foreach ((string key, Color light, Color darkColor) in palette)
            Resources[key] = new SolidColorBrush(dark ? darkColor : light);

        Resources["WindowBackgroundBrush"] = dark
            ? CreateDarkWindowBackground()
            : new SolidColorBrush(Color.FromRgb(0xF4, 0xF5, 0xF7));

        ApplyTitleBarTheme(dark);
    }

    // A soft green-tinted glow radiating from the upper-left, fading to near-black —
    // the "gaming hub" backdrop, in place of a flat dark fill.
    private static RadialGradientBrush CreateDarkWindowBackground() {
        var brush = new RadialGradientBrush {
            GradientOrigin = new System.Windows.Point(0.15, 0.0),
            Center = new System.Windows.Point(0.15, 0.0),
            RadiusX = 1.0,
            RadiusY = 0.9
        };
        brush.GradientStops.Add(new GradientStop(Color.FromRgb(0x16, 0x24, 0x1B), 0.0));
        brush.GradientStops.Add(new GradientStop(Color.FromRgb(0x0A, 0x0B, 0x0D), 0.55));
        return brush;
    }

    // Without this the tray icon sits behind Windows 11's "^" chevron on a machine that hasn't seen
    // it before: started minimized, the app then has no window and no visible icon at all.
    private void PromoteTrayIconOnce(bool retry = true) {
        if (_settings.TrayIconPromoted) return;

        TrayPromotion.Result result = TrayPromotion.TryPromoteOwnIcon();
        if (result == TrayPromotion.Result.NoEntry) {
            // Explorer writes the entry just after the icon first appears, so give it a moment.
            if (!retry) return;
            var later = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
            later.Tick += (_, _) => { later.Stop(); PromoteTrayIconOnce(retry: false); };
            later.Start();
            return;
        }

        if (result == TrayPromotion.Result.Promoted) {
            // Explorer only reads the setting when the icon is added, and it ignores a re-add in the
            // same breath as the write, so the icon goes away and comes back a moment later instead.
            var readd = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            readd.Tick += (_, _) => { readd.Stop(); _tray.Refresh(); };
            readd.Start();
        }
        _settings.TrayIconPromoted = true;
        _settings.Save();
    }

    private void MainWindow_Loaded(object sender, RoutedEventArgs e) {
        PromoteTrayIconOnce();
        _bios.Connect();

        if (!_bios.IsAvailable) {
            ShowBanner(
                "This device doesn't expose the HP BIOS control interface this app relies on " +
                "(root\\wmi, class hpqBIntM). It's built for HP Omen/Victus laptops and needs to run as Administrator.");
            SetControlsEnabled(false);
            _initializing = false;
            return;
        }

        try {
            bool maxFan = _bios.GetMaxFanSpeed();
            MaxFanCheckBox.IsChecked = maxFan;
            _tray.SetMaxFanChecked(maxFan);
        } catch (HpBiosException) {
            // Not fatal — leave the checkbox at its default and let the user try toggling it.
        }

        HpFanMode initialMode = Enum.TryParse(_settings.PerformanceMode, out HpFanMode parsedMode) ? parsedMode : HpFanMode.Balanced;
        SetActiveModeRadio(initialMode);

        if (NvidiaGpuSensor.IsAvailable) {
            GpuTempPanel.ToolTip = null;
            GpuUsagePanel.ToolTip = null;
        }

        PopulateRefreshRateOptions();
        RenderGameProfilesList();
        ScanForGamesIfNoneSaved();
        ShowLastCheckedTime();

        RefreshStats();
        _pollTimer.Start();
        _initializing = false;
        if (SystemTabRadio.IsChecked == true) LoadSystemTab();
        SweepOldDriverDownloads();

        // Applying these after _initializing is cleared so they go through the normal
        // handlers — disabling the mode radios/applying the current power source, and
        // (re)asserting the scheduled task in case it was deleted outside the app.
        if (_settings.AutoPowerSwitch) AutoPowerCheckBox.IsChecked = true;
        if (_settings.StartWithWindows) StartWithWindowsCheckBox.IsChecked = true;
        if (_settings.AutoFanByTemp) AutoFanCheckBox.IsChecked = true;
    }

    private void RefreshStats() {
        if (MemoryInfo.TryGetUsage(out double usedGb, out double totalGb)) {
            int ramPercent = totalGb > 0 ? (int)Math.Round(usedGb / totalGb * 100) : 0;
            RamText.Text = $"{ramPercent}%";
            RamDetailText.Text = $"RAM {usedGb:0.#} / {totalGb:0.#} GB used";
        }

        if (CpuUsageSensor.TryGetUsagePercent(out double cpuUsage))
            CpuUsageText.Text = $"{cpuUsage:0}%";

        RefreshBatteryStatus();
        RefreshGpuStats();
        RefreshIntelGpuStats();

        if (!_bios.IsAvailable) return;

        // Temperature and fan levels are read independently so a failure in one
        // doesn't block the other from updating.
        string tempText = "N/A";
        double? cpuTempValue = null;
        try {
            byte biosTemp = _bios.GetTemperature();
            if (biosTemp > 0) {
                // Some BIOS/model combinations report success but leave this sensor
                // unpopulated (always 0) — fall back to Windows' own ACPI thermal zone.
                tempText = $"{biosTemp}°C";
                cpuTempValue = biosTemp;
            } else if (AcpiThermalSensor.TryRead(out double acpiTemp)) {
                tempText = $"{acpiTemp:0}°C";
                cpuTempValue = acpiTemp;
            }
        } catch (HpBiosException) {
            if (AcpiThermalSensor.TryRead(out double acpiTemp)) {
                tempText = $"{acpiTemp:0}°C";
                cpuTempValue = acpiTemp;
            }
        }
        TemperatureText.Text = tempText;
        UpdateTemperatureGauge(CpuGaugeArc, cpuTempValue);
        UpdateSectionStatus();
        CheckTemperatureAlert("CPU", cpuTempValue, ref _cpuTempAlertActive);
        _lastCpuTempForFan = cpuTempValue;
        ApplyAutoFanCurve();

        try {
            (byte cpu, byte gpu) = _bios.GetFanLevels();
            CpuFanText.Text = $"~{cpu * 100} RPM";
            GpuFanText.Text = $"~{gpu * 100} RPM";

            _tray.SetTooltip($"HP Victus  |  CPU {tempText}  CPU fan {cpu * 100} GPU fan {gpu * 100} RPM");
        } catch (HpBiosException) {
            // Skip this tick; the BIOS occasionally returns a transient error under load.
        }

        CheckGameProfiles();
        KeepWindowsPowerPlanInSync();
    }

    // Windows' power plan can be changed by anything on the machine — another tuning tool, a
    // Windows update, or a stray powercfg call — and the app would go on showing a mode it no
    // longer has. Since the mode selector is what the user trusts, the plan is put back whenever
    // it has drifted away from the selected mode. Reading the active plan is a cheap API call,
    // and nothing is written unless it actually differs.
    private bool _powerPlanChecked;
    private DateTime _lastPowerPlanNotice = DateTime.MinValue;

    private void KeepWindowsPowerPlanInSync() {
        bool corrected = WindowsPowerPlan.EnsureActiveForMode(_currentMode);
        bool firstCheck = !_powerPlanChecked;
        _powerPlanChecked = true;

        // The first check of a session is just the app catching up with whatever the machine was
        // left on — normal, and not worth a notification. After that, a drift means something is
        // quietly undoing the chosen mode, which is worth saying out loud — but only occasionally,
        // in case whatever changed it keeps changing it back.
        if (!corrected || firstCheck || DateTime.UtcNow - _lastPowerPlanNotice < TimeSpan.FromMinutes(5)) return;

        _lastPowerPlanNotice = DateTime.UtcNow;
        _tray.ShowBalloon("HP Victus Control", $"Windows' power plan had changed — put it back to {_currentMode} mode");
    }

    private void RefreshBatteryStatus() {
        var status = System.Windows.Forms.SystemInformation.PowerStatus;
        // No battery at all (desktop, or a battery report Windows can't read) reports 255%.
        if (status.BatteryLifePercent > 1f) {
            BatteryStatusText.Text = "Battery not detected";
            return;
        }

        int percent = (int)Math.Round(status.BatteryLifePercent * 100);
        bool charging = status.BatteryChargeStatus.HasFlag(System.Windows.Forms.BatteryChargeStatus.Charging);
        bool onAc = status.PowerLineStatus == System.Windows.Forms.PowerLineStatus.Online;

        string state = charging ? "Charging" : onAc ? "Plugged in" : "On battery";
        BatteryStatusText.Text = $"Battery {percent}%  ·  {state}";
    }

    private readonly IntelGpuSensor _intelGpu = new();
    private bool _intelGpuQueryInFlight;

    // Reading the GPU Engine counter category walks hundreds of per-process instances, so it runs off the UI thread.
    private async void RefreshIntelGpuStats() {
        if (!_intelGpu.IsAvailable || _intelGpuQueryInFlight) return;

        _intelGpuQueryInFlight = true;
        double? usage = await Task.Run(() => _intelGpu.TryReadUsagePercent());
        _intelGpuQueryInFlight = false;

        if (!usage.HasValue) return;
        IntelGpuDetailText.Text = $"{_intelGpu.Name} {usage.Value:0}% usage";
        IntelGpuDetailText.Visibility = Visibility.Visible;
    }

    // nvidia-smi is a subprocess call, so this runs off the poll tick instead of blocking it.
    private bool _gpuStatsQueryInFlight;

    private async void RefreshGpuStats() {
        if (!NvidiaGpuSensor.IsAvailable || _gpuStatsQueryInFlight) return;

        _gpuStatsQueryInFlight = true;
        (double? temperature, double? utilization, double? powerDraw, double? clockMhz) = await NvidiaGpuSensor.TryReadStatsAsync();
        _gpuStatsQueryInFlight = false;

        GpuTempText.Text = temperature.HasValue ? $"{temperature.Value:0}°C" : "N/A";
        UpdateTemperatureGauge(GpuGaugeArc, temperature);
        UpdateSectionStatus();
        GpuUsageText.Text = utilization.HasValue ? $"{utilization.Value:0}%" : "N/A";

        if (powerDraw.HasValue || clockMhz.HasValue) {
            string power = powerDraw.HasValue ? $"{powerDraw.Value:0}W" : "--W";
            string clock = clockMhz.HasValue ? $"{clockMhz.Value:0} MHz" : "-- MHz";
            GpuDetailText.Text = $"NVIDIA GPU {power}  ·  {clock}";
            GpuDetailText.Visibility = Visibility.Visible;
        }
        CheckTemperatureAlert("GPU", temperature, ref _gpuTempAlertActive);
        _lastGpuTempForFan = temperature;
        ApplyAutoFanCurve();
    }

    // ----- Performance mode ------------------------------------------------------------

    private void ModeRadio_Checked(object sender, RoutedEventArgs e) {
        if (_initializing) return;

        HpFanMode mode =
            ReferenceEquals(sender, PerformanceRadio) ? HpFanMode.Performance :
            ReferenceEquals(sender, CoolRadio) ? HpFanMode.Cool :
            HpFanMode.Balanced;

        ApplyMode(mode);
    }

    private void ApplyMode(HpFanMode mode) {
        HpFanMode previousMode = _currentMode;
        try {
            _bios.SetFanMode(mode);
            _currentMode = mode;
            _tray.SetActiveMode(mode);
            UpdateSectionStatus();

            _settings.PerformanceMode = mode.ToString();
            _settings.Save();

            ApplyRefreshRateForMode(mode);
            WindowsPowerPlan.SetForMode(mode);
            ApplyBrightnessForMode(mode, previousMode);
        } catch (HpBiosException ex) {
            MessageBox.Show(this, ex.Message, "HP Victus Control", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    // Cool mode dims the panel a bit to save power; leaving Cool restores whatever brightness
    // was set before — this never touches brightness for Balanced/Performance on their own,
    // since that's a personal preference the app shouldn't override.
    private byte? _brightnessBeforeCool;

    private void ApplyBrightnessForMode(HpFanMode mode, HpFanMode previousMode) {
        try {
            if (mode == HpFanMode.Cool && previousMode != HpFanMode.Cool) {
                if (ScreenBrightness.TryGetBrightness(out byte current)) {
                    _brightnessBeforeCool = current;
                    byte dimmed = (byte)Math.Min((int)current, 40);
                    _ = SetBrightnessAfterPowerPlanSettlesAsync(dimmed);
                }
            } else if (mode != HpFanMode.Cool && _brightnessBeforeCool.HasValue) {
                byte restore = _brightnessBeforeCool.Value;
                _brightnessBeforeCool = null;
                _ = SetBrightnessAfterPowerPlanSettlesAsync(restore);
            }
        } catch {
            // Best-effort — not every panel supports WMI brightness control.
        }
    }

    // Windows applies each power scheme's own stored brightness (e.g. this machine's "Power
    // saver" scheme is stored at 75% while "Balanced" is at 10%) as part of activating it, with
    // a brief fade — racing an explicit brightness call right after WindowsPowerPlan.SetForMode
    // can get overridden by that fade mid-flight. Waiting it out first makes this call the one
    // that actually sticks.
    private static async Task SetBrightnessAfterPowerPlanSettlesAsync(byte percent) {
        await Task.Delay(500);
        ScreenBrightness.SetBrightness(percent);
    }

    // Each mode remembers its own refresh rate. Performance defaults to the panel's maximum and
    // Balanced/Cool to 60Hz, since a high refresh rate keeps the CPU busier and the laptop hotter.
    private List<int> _refreshRates = new();
    private bool _syncingRefreshRatePicker;

    private int GetSavedRefreshRate(HpFanMode mode) => mode switch {
        HpFanMode.Performance => _settings.PerformanceRefreshRateHz,
        HpFanMode.Cool => _settings.CoolRefreshRateHz,
        _ => _settings.BalancedRefreshRateHz
    };

    private void SaveRefreshRate(HpFanMode mode, int hz) {
        switch (mode) {
            case HpFanMode.Performance: _settings.PerformanceRefreshRateHz = hz; break;
            case HpFanMode.Cool: _settings.CoolRefreshRateHz = hz; break;
            default: _settings.BalancedRefreshRateHz = hz; break;
        }
        _settings.Save();
    }

    private int ResolveRefreshRate(HpFanMode mode) {
        int saved = GetSavedRefreshRate(mode);
        if (saved > 0 && (_refreshRates.Count == 0 || _refreshRates.Contains(saved))) return saved;
        if (_refreshRates.Count == 0) return mode == HpFanMode.Performance ? DisplayRefreshRate.GetMaxRefreshRate() ?? 60 : 60;
        if (mode == HpFanMode.Performance) return _refreshRates[^1];
        return _refreshRates.Contains(60) ? 60 : _refreshRates[0];
    }

    private void ApplyRefreshRateForMode(HpFanMode mode) {
        int rate = ResolveRefreshRate(mode);
        SyncRefreshRatePicker(mode, rate);
        ApplyRefreshRate(rate);
    }

    private void ApplyRefreshRate(int rate) {
        bool applied;
        try {
            applied = DisplayRefreshRate.SetRefreshRate(rate);
        } catch {
            applied = false;
        }
        RefreshRateStatusText.Text = $"Windows didn't accept {rate}Hz for this display.";
        RefreshRateStatusText.Visibility = applied ? Visibility.Collapsed : Visibility.Visible;
    }

    private void PopulateRefreshRateOptions() {
        _refreshRates = DisplayRefreshRate.GetSupportedRefreshRates();
        RefreshRateOptionsPanel.Children.Clear();

        if (_refreshRates.Count <= 1) {
            RefreshRateLabel.Visibility = Visibility.Collapsed;
            RefreshRateTrack.Visibility = Visibility.Collapsed;
            return;
        }

        foreach (int rate in _refreshRates) {
            var radio = new RadioButton {
                Content = $"{rate}Hz", GroupName = "RefreshRateOption", Style = (Style)FindResource("SegmentRadio"),
                Padding = new Thickness(14, 7, 14, 7), Tag = rate
            };
            radio.Checked += RefreshRateOption_Checked;
            RefreshRateOptionsPanel.Children.Add(radio);
        }

        SyncRefreshRatePicker(_currentMode, ResolveRefreshRate(_currentMode));
    }

    private void SyncRefreshRatePicker(HpFanMode mode, int rate) {
        RefreshRateLabel.Text = $"Refresh rate in {mode} mode";
        _syncingRefreshRatePicker = true;
        foreach (RadioButton radio in RefreshRateOptionsPanel.Children.OfType<RadioButton>())
            radio.IsChecked = (int)radio.Tag == rate;
        _syncingRefreshRatePicker = false;
    }

    private void RefreshRateOption_Checked(object sender, RoutedEventArgs e) {
        if (_initializing || _syncingRefreshRatePicker) return;

        int rate = (int)((RadioButton)sender).Tag;
        SaveRefreshRate(_currentMode, rate);
        ApplyRefreshRate(rate);
    }

    // Sets which radio button is shown as active. Note the BIOS interface only exposes a way
    // to *set* the performance mode, not read the current one back, so on startup this is just
    // a UI default (Balanced) rather than the laptop's actual current mode.
    private void SetActiveModeRadio(HpFanMode mode) {
        _currentMode = mode;
        _tray.SetActiveMode(mode);
        UpdateSectionStatus();

        // Setting IsChecked=true raises the Checked event below, which calls ApplyMode()
        // itself once (skipped during startup init) — don't call it again here.
        BalancedRadio.IsChecked = mode == HpFanMode.Balanced;
        PerformanceRadio.IsChecked = mode == HpFanMode.Performance;
        CoolRadio.IsChecked = mode == HpFanMode.Cool;
    }

    // ----- Auto-switch by power source -----------------------------------------------------

    private void AutoPowerCheckBox_Changed(object sender, RoutedEventArgs e) {
        if (_initializing) return;

        bool auto = AutoPowerCheckBox.IsChecked == true;
        BalancedRadio.IsEnabled = !auto;
        PerformanceRadio.IsEnabled = !auto;
        CoolRadio.IsEnabled = !auto;

        if (auto) ApplyModeForCurrentPowerSource();

        _settings.AutoPowerSwitch = auto;
        _settings.Save();
    }

    private void SystemEvents_PowerModeChanged(object? sender, Microsoft.Win32.PowerModeChangedEventArgs e) {
        if (e.Mode != Microsoft.Win32.PowerModes.StatusChange) return;
        Dispatcher.Invoke(() => {
            if (AutoPowerCheckBox.IsChecked == true) ApplyModeForCurrentPowerSource();
        });
    }

    private void ApplyModeForCurrentPowerSource() {
        bool onAc = System.Windows.Forms.SystemInformation.PowerStatus.PowerLineStatus
            == System.Windows.Forms.PowerLineStatus.Online;
        SetActiveModeRadio(onAc ? HpFanMode.Performance : HpFanMode.Cool);
    }

    // ----- Manual fan speed --------------------------------------------------------------

    private void ManualCheckBox_Changed(object sender, RoutedEventArgs e) {
        if (_initializing) return;

        bool manual = ManualCheckBox.IsChecked == true;
        ApplyFanButton.IsEnabled = manual;

        if (manual && AutoFanCheckBox.IsChecked == true) AutoFanCheckBox.IsChecked = false;

        if (!manual) {
            try {
                _bios.ReleaseManualFanControl();
            } catch (HpBiosException ex) {
                MessageBox.Show(this, ex.Message, "HP Victus Control", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
    }

    private void FanSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e) {
        CpuFanSliderText.Text = ((int)CpuFanSlider.Value).ToString();
        GpuFanSliderText.Text = ((int)GpuFanSlider.Value).ToString();
    }

    private void ApplyFanButton_Click(object sender, RoutedEventArgs e) {
        try {
            _bios.SetFanLevels((byte)CpuFanSlider.Value, (byte)GpuFanSlider.Value);
        } catch (HpBiosException ex) {
            MessageBox.Show(this, ex.Message, "HP Victus Control", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void MaxFanCheckBox_Changed(object sender, RoutedEventArgs e) {
        if (_initializing) return;

        bool enabled = MaxFanCheckBox.IsChecked == true;
        if (enabled && AutoFanCheckBox.IsChecked == true) AutoFanCheckBox.IsChecked = false;

        try {
            _bios.SetMaxFanSpeed(enabled);
            _tray.SetMaxFanChecked(enabled);
        } catch (HpBiosException ex) {
            MessageBox.Show(this, ex.Message, "HP Victus Control", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    // ----- Auto fan by temperature ----------------------------------------------------------

    private double? _lastCpuTempForFan;
    private double? _lastGpuTempForFan;
    private byte? _lastAutoCpuFanLevel;
    private byte? _lastAutoGpuFanLevel;

    private void AutoFanCheckBox_Changed(object sender, RoutedEventArgs e) {
        if (_initializing) return;

        bool auto = AutoFanCheckBox.IsChecked == true;
        _settings.AutoFanByTemp = auto;
        _settings.Save();

        if (auto) {
            if (ManualCheckBox.IsChecked == true) ManualCheckBox.IsChecked = false;
            if (MaxFanCheckBox.IsChecked == true) {
                MaxFanCheckBox.IsChecked = false;
            } else {
                // Force max's own handler already releases control when it's the one turning
                // off; otherwise hand control back to the BIOS curve before we start driving it.
                try { _bios.ReleaseManualFanControl(); } catch (HpBiosException) { }
            }
            _lastAutoCpuFanLevel = null;
            _lastAutoGpuFanLevel = null;
        } else {
            try { _bios.ReleaseManualFanControl(); } catch (HpBiosException) { }
        }
    }

    // Simple staged curve: quieter at low temps, ramping up toward max as things get hot.
    // Applied independently per fan based on that fan's own component temperature.
    private static byte FanLevelForTemperature(double celsius) => celsius switch {
        < 45 => (byte)10,
        < 55 => (byte)18,
        < 65 => (byte)27,
        < 75 => (byte)37,
        < 85 => (byte)48,
        _ => (byte)60
    };

    private void ApplyAutoFanCurve() {
        if (AutoFanCheckBox.IsChecked != true || _fanTestRunning) return;

        byte cpuLevel = _lastCpuTempForFan.HasValue ? FanLevelForTemperature(_lastCpuTempForFan.Value) : (byte)27;
        byte gpuLevel = _lastGpuTempForFan.HasValue ? FanLevelForTemperature(_lastGpuTempForFan.Value) : cpuLevel;

        if (_lastAutoCpuFanLevel == cpuLevel && _lastAutoGpuFanLevel == gpuLevel) return;

        try {
            _bios.SetFanLevels(cpuLevel, gpuLevel);
            _lastAutoCpuFanLevel = cpuLevel;
            _lastAutoGpuFanLevel = gpuLevel;
        } catch (HpBiosException) {
            // Skip this tick; the next poll will retry.
        }
    }

    // ----- Driver & BIOS updates -----------------------------------------------------------

    private sealed class UpdateEntry {
        public required HpDriverUpdate Update;
        public required CheckBox SelectCheckBox;
        public required Button DownloadButton;
        public required Border RowBorder;
        public required Grid RowGrid;
        public required TextBlock DateText;
        public required StackPanel ActionPanel;
        public string? DownloadedPath;
        public bool IsBusy;
    }

    private readonly List<UpdateEntry> _updateEntries = new();
    private List<HpDriverUpdate> _lastUpdates = new();
    private bool _updatingSelectAll;

    private void CheckUpdatesButton_Click(object sender, RoutedEventArgs e) => _ = CheckForUpdatesAsync();

    private void ShowLastCheckedTime() {
        if (_settings.LastUpdateCheckUtc is not DateTime lastChecked) return;
        TimeSpan age = DateTime.UtcNow - lastChecked;
        string ago = age.TotalMinutes < 1 ? "just now"
            : age.TotalHours < 1 ? $"{(int)age.TotalMinutes} min ago"
            : age.TotalDays < 1 ? $"{(int)age.TotalHours} hr ago"
            : $"{(int)age.TotalDays} day(s) ago";
        LastCheckedText.Text = $"Last checked {ago}";
        LastCheckedText.Visibility = Visibility.Visible;
    }

    private void OpenDownloadsFolderButton_Click(object sender, RoutedEventArgs e) {
        try {
            string folder = Maintenance.DriverDownloadsFolder;
            Directory.CreateDirectory(folder);
            Process.Start(new ProcessStartInfo(folder) { UseShellExecute = true });
        } catch {
            // Best-effort — worst case nothing happens and the user can navigate there manually.
        }
    }

    private async Task CheckForUpdatesAsync() {
        CheckUpdatesButton.IsEnabled = false;
        UpdatesListPanel.Children.Clear();
        _updateEntries.Clear();
        _lastUpdates = new List<HpDriverUpdate>();
        _downloadedPaths.Clear();
        CloseUpdateDetails();
        UpdatesTableCard.Visibility = Visibility.Collapsed;
        UpdatesTitleText.Text = "Driver & BIOS updates";
        UpdatesStatusText.Text = "Checking HP's support site for your exact model...";

        try {
            string serial = HpDriverUpdateService.GetLocalSerialNumber();
            List<HpDriverUpdate> rawUpdates = await HpDriverUpdateService.GetUpdatesForSerialAsync(serial);
            List<HpDriverUpdate> updates = HpDriverUpdateService.GetActionableUpdates(rawUpdates);

            if (NvidiaGpuSensor.IsAvailable) {
                UpdatesStatusText.Text = "Checking NVIDIA directly for the real latest GPU driver...";
                HpDriverUpdate? nvidiaUpdate = await NvidiaDriverUpdateService.GetUpdateIfNewerAsync();
                if (nvidiaUpdate != null) updates.Add(nvidiaUpdate);
            }

            UpdatesStatusText.Text = "Checking Intel directly for newer Intel drivers...";
            List<IntelDriverMatch> intelMatches = await IntelDriverUpdateService.CheckAsync();
            updates.AddRange(intelMatches.Where(match => match.IsNewer).Select(match => match.Latest));
            string intelCurrent = string.Join(", ", intelMatches.Where(match => !match.IsNewer)
                .Select(match => $"{match.DeviceName} {match.InstalledVersion}"));

            if (updates.Count == 0) {
                UpdatesTitleText.Text = "You're up to date";
                UpdatesStatusText.Text = "No newer drivers or BIOS found for this model."
                    + (intelCurrent.Length > 0 ? $" Intel confirms its latest is installed: {intelCurrent}." : "");
            } else {
                _lastUpdates = updates;
                UpdatesTitleText.Text = updates.Count == 1 ? "1 update available" : $"{updates.Count} updates available";
                UpdatesStatusText.Text = DescribeUpdateMix(updates);
                RenderUpdateRows(updates);
                UpdatesTableCard.Visibility = Visibility.Visible;
            }

            _settings.LastUpdateCheckUtc = DateTime.UtcNow;
            _settings.Save();
            ShowLastCheckedTime();
        } catch (HpUpdateException ex) {
            UpdatesStatusText.Text = $"Couldn't check for updates: {ex.Message}";
        } catch (Exception ex) {
            UpdatesStatusText.Text = $"Unexpected error checking for updates: {ex.Message}";
        } finally {
            CheckUpdatesButton.IsEnabled = true;
        }
    }

    // ----- Updates list: filters, search, sorting, type tags -----

    private enum UpdateSortColumn { Name, Type, Version, Size, Released }

    private enum UpdateFilter { All, Recommended, Bios, Firmware, Driver, Software, Other, Downloaded }

    private UpdateSortColumn _updateSort = UpdateSortColumn.Released;
    private bool _updateSortDescending = true;
    private UpdateFilter _updateFilter = UpdateFilter.All;
    private string _updateSearch = "";
    private HpDriverUpdate? _shownUpdate;

    // Downloads survive re-rendering (and filtering a row out), so the paths live here, not in the rows.
    private readonly Dictionary<HpDriverUpdate, string> _downloadedPaths = new();

    // One button cycles the order instead of five clickable column headers.
    private static readonly (UpdateSortColumn Column, bool Descending, string Label)[] UpdateSortCycle = {
        (UpdateSortColumn.Released, true, "Newest first"),
        (UpdateSortColumn.Released, false, "Oldest first"),
        (UpdateSortColumn.Name, false, "Name A→Z"),
        (UpdateSortColumn.Type, false, "By type"),
        (UpdateSortColumn.Size, true, "Largest first"),
    };

    private void UpdateSortButton_Click(object sender, RoutedEventArgs e) {
        int current = Array.FindIndex(UpdateSortCycle, x => x.Column == _updateSort && x.Descending == _updateSortDescending);
        (_updateSort, _updateSortDescending, _) = UpdateSortCycle[(current + 1) % UpdateSortCycle.Length];
        if (_lastUpdates.Count > 0) RenderUpdateRows(_lastUpdates);
    }

    private void UpdateSearchBox_TextChanged(object sender, TextChangedEventArgs e) {
        _updateSearch = UpdateSearchBox.Text.Trim();
        UpdateSearchHint.Visibility = _updateSearch.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (_lastUpdates.Count > 0) RenderUpdateRows(_lastUpdates);
    }

    // BIOS, firmware and drivers are the ones worth installing; HP's utilities are optional extras.
    private static bool IsRecommendedUpdate(HpDriverUpdate update) =>
        DescribeUpdateType(update.Category).Kind is UpdateKind.Bios or UpdateKind.Firmware or UpdateKind.Driver;

    private bool PassesUpdateFilter(HpDriverUpdate update) {
        UpdateKind kind = DescribeUpdateType(update.Category).Kind;
        bool matchesFilter = _updateFilter switch {
            UpdateFilter.All => true,
            UpdateFilter.Recommended => IsRecommendedUpdate(update),
            UpdateFilter.Bios => kind == UpdateKind.Bios,
            UpdateFilter.Firmware => kind == UpdateKind.Firmware,
            UpdateFilter.Driver => kind == UpdateKind.Driver,
            UpdateFilter.Software => kind == UpdateKind.Software,
            UpdateFilter.Other => kind == UpdateKind.Other,
            _ => _downloadedPaths.ContainsKey(update),
        };
        if (!matchesFilter) return false;
        if (_updateSearch.Length == 0) return true;

        return update.Title.Contains(_updateSearch, StringComparison.CurrentCultureIgnoreCase)
            || update.Category.Contains(_updateSearch, StringComparison.CurrentCultureIgnoreCase)
            || (update.Version ?? "").Contains(_updateSearch, StringComparison.CurrentCultureIgnoreCase);
    }

    private void RenderUpdateFilters(List<HpDriverUpdate> updates) {
        UpdateFiltersPanel.Children.Clear();

        AddFilterGroupLabel("Show", isFirst: true);
        AddFilterRow(UpdateFilter.All, "All updates", updates.Count);
        AddFilterRow(UpdateFilter.Recommended, "Recommended", updates.Count(IsRecommendedUpdate));

        AddFilterGroupLabel("Type");
        (UpdateFilter Filter, string Label, UpdateKind Kind)[] kinds = {
            (UpdateFilter.Bios, "BIOS", UpdateKind.Bios), (UpdateFilter.Firmware, "Firmware", UpdateKind.Firmware),
            (UpdateFilter.Driver, "Drivers", UpdateKind.Driver), (UpdateFilter.Software, "Software", UpdateKind.Software),
            (UpdateFilter.Other, "Other", UpdateKind.Other),
        };
        foreach ((UpdateFilter filter, string label, UpdateKind kind) in kinds) {
            int count = updates.Count(u => DescribeUpdateType(u.Category).Kind == kind);
            // An empty type is left out entirely, unless it's the filter the user is looking at.
            if (count > 0 || _updateFilter == filter) AddFilterRow(filter, label, count);
        }

        if (_downloadedPaths.Count > 0 || _updateFilter == UpdateFilter.Downloaded) {
            AddFilterGroupLabel("State");
            AddFilterRow(UpdateFilter.Downloaded, "Downloaded", updates.Count(_downloadedPaths.ContainsKey));
        }
    }

    private void AddFilterGroupLabel(string text, bool isFirst = false) {
        var label = new TextBlock {
            Text = text.ToUpperInvariant(), FontSize = 10, FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(8, isFirst ? 6 : 12, 8, 4)
        };
        label.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
        UpdateFiltersPanel.Children.Add(label);
    }

    private void AddFilterRow(UpdateFilter filter, string label, int count) {
        bool active = _updateFilter == filter;

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var text = new TextBlock {
            Text = label, FontSize = 12, VerticalAlignment = VerticalAlignment.Center,
            FontWeight = active ? FontWeights.SemiBold : FontWeights.Normal, TextTrimming = TextTrimming.CharacterEllipsis
        };
        text.SetResourceReference(TextBlock.ForegroundProperty, active ? "AccentBrush" : "TextPrimaryBrush");
        grid.Children.Add(text);

        var countText = new TextBlock { Text = count.ToString(), FontSize = 11, VerticalAlignment = VerticalAlignment.Center };
        countText.SetResourceReference(TextBlock.ForegroundProperty, active ? "AccentBrush" : "TextSecondaryBrush");
        var pill = new Border {
            CornerRadius = new CornerRadius(6), Padding = new Thickness(6, 1, 6, 1),
            VerticalAlignment = VerticalAlignment.Center, Child = countText
        };
        pill.SetResourceReference(Border.BackgroundProperty, "TrackBrush");
        Grid.SetColumn(pill, 1);
        grid.Children.Add(pill);

        var rowBorder = new Border {
            CornerRadius = new CornerRadius(7), Padding = new Thickness(8, 7, 8, 7), Margin = new Thickness(0, 1, 0, 1),
            Background = System.Windows.Media.Brushes.Transparent, Cursor = System.Windows.Input.Cursors.Hand, Child = grid
        };
        if (active) rowBorder.SetResourceReference(Border.BackgroundProperty, "CardBackgroundBrush");
        rowBorder.MouseLeftButtonUp += (_, _) => {
            if (_updateFilter == filter) return;
            _updateFilter = filter;
            if (_lastUpdates.Count > 0) RenderUpdateRows(_lastUpdates);
        };
        UpdateFiltersPanel.Children.Add(rowBorder);
    }

    private enum UpdateKind { Bios, Firmware, Driver, Software, Other }

    // HP's categories read like "Driver-Graphics", "Software-HP Cloud Recovery" or "Utility-Tools";
    // the tag shows the useful half and the colour shows what kind of update it is.
    private static (string Label, UpdateKind Kind) DescribeUpdateType(string category) {
        string text = category.Trim();
        if (text.Contains("BIOS", StringComparison.OrdinalIgnoreCase)) return ("BIOS", UpdateKind.Bios);
        if (text.Contains("Firmware", StringComparison.OrdinalIgnoreCase)) return ("Firmware", UpdateKind.Firmware);

        string[] parts = text.Split('-', 2, StringSplitOptions.TrimEntries);
        string head = parts[0];
        if (head.Equals("Driver", StringComparison.OrdinalIgnoreCase))
            return (parts.Length > 1 && parts[1].Length > 0 ? parts[1] : "Driver", UpdateKind.Driver);
        if (head.StartsWith("Software", StringComparison.OrdinalIgnoreCase) || head.StartsWith("Utility", StringComparison.OrdinalIgnoreCase))
            return (head, UpdateKind.Software);
        return (head.Length > 0 ? head : "Other", UpdateKind.Other);
    }

    // Tag colours: firmware amber and diagnostics/other grey as in the chosen design, plus red for
    // BIOS (the one to be careful with), blue for drivers and purple for HP software.
    private (Color Background, Color Foreground) UpdateTagColors(UpdateKind kind) {
        bool dark = DarkThemeCheckBox.IsChecked == true;
        return kind switch {
            UpdateKind.Bios => dark ? (Color.FromRgb(0x42, 0x17, 0x17), Color.FromRgb(0xF0, 0x95, 0x95)) : (Color.FromRgb(0xFC, 0xEB, 0xEB), Color.FromRgb(0xA3, 0x2D, 0x2D)),
            UpdateKind.Firmware => dark ? (Color.FromRgb(0x3A, 0x2A, 0x0C), Color.FromRgb(0xEF, 0x9F, 0x27)) : (Color.FromRgb(0xFA, 0xEE, 0xDA), Color.FromRgb(0x85, 0x4F, 0x0B)),
            UpdateKind.Driver => dark ? (Color.FromRgb(0x0C, 0x24, 0x40), Color.FromRgb(0x85, 0xB7, 0xEB)) : (Color.FromRgb(0xE6, 0xF1, 0xFB), Color.FromRgb(0x18, 0x5F, 0xA5)),
            UpdateKind.Software => dark ? (Color.FromRgb(0x26, 0x21, 0x5C), Color.FromRgb(0xAF, 0xA9, 0xEC)) : (Color.FromRgb(0xEE, 0xED, 0xFE), Color.FromRgb(0x53, 0x4A, 0xB7)),
            _ => dark ? (Color.FromRgb(0x2C, 0x2C, 0x2A), Color.FromRgb(0xB4, 0xB2, 0xA9)) : (Color.FromRgb(0xF1, 0xEF, 0xE8), Color.FromRgb(0x5F, 0x5E, 0x5A)),
        };
    }

    private static string DescribeUpdateMix(List<HpDriverUpdate> updates) {
        static string Noun(UpdateKind kind, int count) => kind switch {
            UpdateKind.Bios => "BIOS",
            UpdateKind.Firmware => "firmware",
            UpdateKind.Driver => count == 1 ? "driver" : "drivers",
            UpdateKind.Software => "software",
            _ => "other"
        };

        IEnumerable<string> counts = updates
            .GroupBy(u => DescribeUpdateType(u.Category).Kind)
            .OrderBy(g => g.Key)
            .Select(g => $"{g.Count()} {Noun(g.Key, g.Count())}");
        return string.Join("  ·  ", counts);
    }

    private static double ParseSizeMegabytes(string size) {
        System.Text.RegularExpressions.Match match =
            System.Text.RegularExpressions.Regex.Match(size ?? "", @"([\d.,]+)\s*(KB|MB|GB)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (!match.Success || !double.TryParse(match.Groups[1].Value.Replace(",", ""), System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out double value)) return 0;
        return match.Groups[2].Value.ToUpperInvariant() switch { "KB" => value / 1024, "GB" => value * 1024, _ => value };
    }

    private static string OrDash(string? text) => string.IsNullOrWhiteSpace(text) || text == "N/A" ? "—" : text;

    private void RenderUpdateRows(List<HpDriverUpdate> updates) {
        // Re-sorting and filtering rebuild the rows, so carry over what the user already ticked.
        HashSet<HpDriverUpdate> selected = _updateEntries.Where(x => x.SelectCheckBox.IsChecked == true).Select(x => x.Update).ToHashSet();

        UpdatesListPanel.Children.Clear();
        _updateEntries.Clear();

        RenderUpdateFilters(updates);
        UpdateSortButton.Content = UpdateSortCycle
            .First(x => x.Column == _updateSort && x.Descending == _updateSortDescending).Label;

        IEnumerable<HpDriverUpdate> ordered = _updateSort switch {
            UpdateSortColumn.Name => updates.OrderBy(u => u.Title, StringComparer.CurrentCultureIgnoreCase),
            UpdateSortColumn.Type => updates.OrderBy(u => DescribeUpdateType(u.Category).Kind).ThenBy(u => DescribeUpdateType(u.Category).Label, StringComparer.OrdinalIgnoreCase),
            UpdateSortColumn.Version => updates.OrderBy(u => u.Version, StringComparer.OrdinalIgnoreCase),
            UpdateSortColumn.Size => updates.OrderBy(u => ParseSizeMegabytes(u.FileSize)),
            _ => updates.OrderBy(u => HpDriverUpdateService.ParseReleaseDate(u.ReleaseDate)),
        };
        if (_updateSortDescending) ordered = ordered.Reverse();

        List<HpDriverUpdate> visible = ordered.Where(PassesUpdateFilter).ToList();
        bool first = true;
        foreach (HpDriverUpdate update in visible) {
            UpdateEntry entry = AddUpdateRow(update, first);
            first = false;

            if (selected.Contains(update)) entry.SelectCheckBox.IsChecked = true;
            if (_downloadedPaths.TryGetValue(update, out string? path)) {
                entry.DownloadedPath = path;
                ShowDownloadedButtons(entry);
            }
        }

        if (visible.Count == 0) {
            var empty = new TextBlock {
                Text = "Nothing matches this filter.", FontSize = 12, Margin = new Thickness(0, 24, 0, 24),
                HorizontalAlignment = System.Windows.HorizontalAlignment.Center
            };
            empty.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
            UpdatesListPanel.Children.Add(empty);
        }

        // The open details pane belongs to a row that may have just been filtered away.
        if (_shownUpdate != null && !visible.Contains(_shownUpdate)) CloseUpdateDetails();

        UpdateBulkButtonsState();
    }

    private UpdateEntry AddUpdateRow(HpDriverUpdate update, bool isFirst) {
        var row = new Grid();
        foreach (GridLength width in new[] {
            new GridLength(30), new GridLength(1, GridUnitType.Star), new GridLength(100), new GridLength(200)
        }) row.ColumnDefinitions.Add(new ColumnDefinition { Width = width });

        var selectCheckBox = new CheckBox { Style = (Style)FindResource("ModernCheckBox"), VerticalAlignment = VerticalAlignment.Center };
        row.Children.Add(selectCheckBox);

        (string typeLabel, UpdateKind kind) = DescribeUpdateType(update.Category);
        (Color tagBackground, Color tagForeground) = UpdateTagColors(kind);
        var tag = new Border {
            Background = new SolidColorBrush(tagBackground), CornerRadius = new CornerRadius(6), Padding = new Thickness(7, 2, 7, 2),
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0), ToolTip = update.Category,
            Child = new TextBlock {
                Text = typeLabel, FontSize = 10, FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(tagForeground), TextTrimming = TextTrimming.CharacterEllipsis
            }
        };

        // Wrapping rather than trimming: with the details pane open there isn't much room, and a
        // driver name cut to "NVI..." tells the user nothing.
        var nameText = new TextBlock {
            Text = update.Title, FontWeight = FontWeights.SemiBold, FontSize = 12, ToolTip = update.Title,
            VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap
        };
        nameText.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimaryBrush");

        var titleLine = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(tag, Dock.Left);
        titleLine.Children.Add(tag);
        titleLine.Children.Add(nameText);

        var subText = new TextBlock {
            Text = $"{OrDash(update.Version)}  ·  {OrDash(update.FileSize)}", FontSize = 11,
            Margin = new Thickness(0, 3, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis
        };
        subText.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");

        var contentPanel = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) };
        contentPanel.Children.Add(titleLine);
        contentPanel.Children.Add(subText);
        Grid.SetColumn(contentPanel, 1);
        row.Children.Add(contentPanel);

        var dateText = new TextBlock {
            Text = OrDash(update.ReleaseDate), FontSize = 11, VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 0, 10, 0)
        };
        dateText.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
        Grid.SetColumn(dateText, 2);
        row.Children.Add(dateText);

        var actionPanel = new StackPanel {
            Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Right
        };
        Grid.SetColumn(actionPanel, 3);
        row.Children.Add(actionPanel);

        var downloadButton = new Button { Content = "Download", Style = (Style)FindResource("OutlineAccentButton") };
        actionPanel.Children.Add(downloadButton);

        var rowBorder = new Border {
            BorderThickness = new Thickness(0, isFirst ? 0 : 1, 0, 0), Padding = new Thickness(8, 10, 8, 10),
            CornerRadius = new CornerRadius(7), Background = System.Windows.Media.Brushes.Transparent,
            Cursor = System.Windows.Input.Cursors.Hand, ToolTip = "Click for details", Child = row
        };
        rowBorder.SetResourceReference(Border.BorderBrushProperty, "BorderBrush2");

        var entry = new UpdateEntry {
            Update = update, SelectCheckBox = selectCheckBox, DownloadButton = downloadButton,
            ActionPanel = actionPanel, RowBorder = rowBorder, RowGrid = row, DateText = dateText
        };
        _updateEntries.Add(entry);
        ApplyUpdateRowDensity(entry);

        selectCheckBox.Checked += (_, _) => UpdateBulkButtonsState();
        selectCheckBox.Unchecked += (_, _) => UpdateBulkButtonsState();
        downloadButton.Click += async (_, _) => await DownloadEntryAsync(entry);
        rowBorder.MouseLeftButtonUp += (_, _) => ShowUpdateDetails(update);

        UpdatesListPanel.Children.Add(rowBorder);
        if (update == _shownUpdate) HighlightUpdateRows();
        return entry;
    }

    // The release date is the first thing to go when the list is squeezed — by a narrow window, or
    // by the details pane, which shows the date anyway.
    private bool _updatesCompact;

    private void UpdatesListPanel_SizeChanged(object sender, SizeChangedEventArgs e) {
        bool compact = e.NewSize.Width < 540;
        if (compact == _updatesCompact) return;
        _updatesCompact = compact;
        foreach (UpdateEntry entry in _updateEntries) ApplyUpdateRowDensity(entry);
    }

    private void ApplyUpdateRowDensity(UpdateEntry entry) {
        entry.RowGrid.ColumnDefinitions[2].Width = new GridLength(_updatesCompact ? 0 : 100);
        entry.DateText.Visibility = _updatesCompact ? Visibility.Collapsed : Visibility.Visible;
    }

    // ----- Details pane -----

    private void HighlightUpdateRows() {
        foreach (UpdateEntry entry in _updateEntries) {
            if (entry.Update == _shownUpdate) entry.RowBorder.SetResourceReference(Border.BackgroundProperty, "TrackBrush");
            else entry.RowBorder.Background = System.Windows.Media.Brushes.Transparent;
        }
    }

    private void ShowUpdateDetails(HpDriverUpdate update) {
        _shownUpdate = update;
        HighlightUpdateRows();

        (string typeLabel, UpdateKind kind) = DescribeUpdateType(update.Category);
        UpdateDetailsTitle.Text = update.Title;
        UpdateDetailsSubtitle.Text = typeLabel.Equals(update.Category, StringComparison.OrdinalIgnoreCase)
            ? typeLabel : $"{typeLabel}  ·  {update.Category}";
        UpdateDetailsContent.Children.Clear();

        if (kind == UpdateKind.Bios) {
            AddDetailsNotice(kind, "Plug in the charger and leave it running. An interrupted BIOS update can leave the "
                + "laptop unable to boot — this app refuses to start one below 50% battery or on battery power.");
        } else if (kind == UpdateKind.Firmware) {
            AddDetailsNotice(kind, "Firmware is written to the device itself. Keep the laptop powered while it installs.");
        }

        AddDetailFact("Offered version", OrDash(update.Version));
        if (kind == UpdateKind.Bios && InstalledDriverInfo.GetBiosVersion() is string installedBios)
            AddDetailFact("Installed now", installedBios);
        AddDetailFact("Download size", OrDash(update.FileSize));
        AddDetailFact("Released", OrDash(update.ReleaseDate));
        AddDetailFact("Source", DescribeUpdateSource(update));
        AddDetailFact("File", OrDash(update.FileName));

        if (_downloadedPaths.TryGetValue(update, out string? savedPath)) {
            AddDetailFact("Saved to", savedPath);

            var openButton = new Button {
                Content = "Open folder", Style = (Style)FindResource("SecondaryButton"),
                Padding = new Thickness(12, 7, 12, 7), Margin = new Thickness(0, 12, 0, 0),
                HorizontalAlignment = System.Windows.HorizontalAlignment.Stretch
            };
            openButton.Click += (_, _) =>
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{savedPath}\"") { UseShellExecute = true });
            UpdateDetailsContent.Children.Add(openButton);
        } else {
            var downloadButton = new Button {
                Content = "Download", Style = (Style)FindResource("PrimaryButton"),
                Padding = new Thickness(12, 7, 12, 7), Margin = new Thickness(0, 12, 0, 0),
                HorizontalAlignment = System.Windows.HorizontalAlignment.Stretch
            };
            downloadButton.Click += async (_, _) => {
                UpdateEntry? entry = _updateEntries.FirstOrDefault(x => x.Update == update);
                if (entry != null) await DownloadEntryAsync(entry);
            };
            UpdateDetailsContent.Children.Add(downloadButton);
        }

        UpdateDetailsPane.Visibility = Visibility.Visible;
    }

    private void CloseUpdateDetailsButton_Click(object sender, RoutedEventArgs e) => CloseUpdateDetails();

    private void CloseUpdateDetails() {
        _shownUpdate = null;
        UpdateDetailsPane.Visibility = Visibility.Collapsed;
        UpdateDetailsContent.Children.Clear();
        HighlightUpdateRows();
    }

    // HP's own list, or the NVIDIA/Intel checks the app makes directly — worth showing before installing.
    private static string DescribeUpdateSource(HpDriverUpdate update) {
        try {
            string host = new Uri(update.DownloadUrl).Host;
            return host.Contains("nvidia", StringComparison.OrdinalIgnoreCase) ? $"NVIDIA ({host})"
                : host.Contains("intel", StringComparison.OrdinalIgnoreCase) ? $"Intel ({host})"
                : host.Contains("hp.com", StringComparison.OrdinalIgnoreCase) ? $"HP ({host})"
                : host;
        } catch {
            return "—";
        }
    }

    private void AddDetailsNotice(UpdateKind kind, string text) {
        (Color background, Color foreground) = UpdateTagColors(kind);
        UpdateDetailsContent.Children.Add(new Border {
            Background = new SolidColorBrush(background), CornerRadius = new CornerRadius(7),
            Padding = new Thickness(10, 8, 10, 8), Margin = new Thickness(0, 0, 0, 12),
            Child = new TextBlock {
                Text = text, FontSize = 11, TextWrapping = TextWrapping.Wrap, Foreground = new SolidColorBrush(foreground)
            }
        });
    }

    private void AddDetailFact(string label, string value) {
        var labelText = new TextBlock { Text = label, FontSize = 10, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 8, 0, 1) };
        labelText.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");

        var valueText = new TextBlock { Text = value, FontSize = 12, TextWrapping = TextWrapping.Wrap, ToolTip = value };
        valueText.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimaryBrush");

        UpdateDetailsContent.Children.Add(labelText);
        UpdateDetailsContent.Children.Add(valueText);
    }

    private void SelectAllCheckBox_Changed(object sender, RoutedEventArgs e) {
        if (_updatingSelectAll) return;
        bool check = SelectAllCheckBox.IsChecked == true;
        foreach (UpdateEntry entry in _updateEntries) entry.SelectCheckBox.IsChecked = check;
    }

    private void UpdateBulkButtonsState() {
        int selected = _updateEntries.Count(x => x.SelectCheckBox.IsChecked == true);
        DownloadSelectedButton.IsEnabled = selected > 0;
        InstallSelectedButton.IsEnabled = selected > 0;
        DownloadSelectedButton.Content = selected > 0 ? $"Download selected ({selected})" : "Download selected";
        InstallSelectedButton.Content = selected > 0 ? $"Install selected ({selected})" : "Install selected";
        SelectionSummaryText.Text = selected > 0
            ? $"{selected} of {_updateEntries.Count} selected"
            : "Tick updates to download or install several at once. Click an update for details.";

        _updatingSelectAll = true;
        SelectAllCheckBox.IsChecked = selected > 0 && selected == _updateEntries.Count;
        _updatingSelectAll = false;
    }

    private async Task DownloadEntryAsync(UpdateEntry entry) {
        if (entry.DownloadedPath != null || entry.IsBusy) return;

        entry.IsBusy = true;
        entry.DownloadButton.IsEnabled = false;
        entry.DownloadButton.Content = "Downloading...";
        try {
            entry.DownloadedPath = await HpDriverUpdateService.DownloadUpdateAsync(entry.Update, Maintenance.DriverDownloadsFolder);
            ShowDownloadedButtons(entry);
        } catch (Exception ex) {
            entry.DownloadButton.Content = "Retry";
            entry.DownloadButton.IsEnabled = true;
            MessageBox.Show(this, $"Download failed: {ex.Message}", "HP Victus Control", MessageBoxButton.OK, MessageBoxImage.Warning);
        } finally {
            entry.IsBusy = false;
        }
    }

    private void ShowDownloadedButtons(UpdateEntry entry) {
        _downloadedPaths[entry.Update] = entry.DownloadedPath!;
        entry.ActionPanel.Children.Clear();

        var openButton = new Button {
            Content = "Open folder", Style = (Style)FindResource("SecondaryButton"),
            Padding = new Thickness(10, 6, 10, 6), Margin = new Thickness(0, 0, 8, 0)
        };
        openButton.Click += (_, _) =>
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{entry.DownloadedPath}\"") { UseShellExecute = true });

        var runButton = new Button {
            Content = "Run installer", Style = (Style)FindResource("PrimaryButton"),
            Padding = new Thickness(14, 6, 14, 6)
        };
        runButton.Click += async (_, _) => await RunInstallerAsync(entry.Update, entry.DownloadedPath!, runButton);

        entry.ActionPanel.Children.Add(openButton);
        entry.ActionPanel.Children.Add(runButton);

        // The details pane shows where the file landed, so redraw it if this is the update it's showing.
        if (_shownUpdate == entry.Update) ShowUpdateDetails(entry.Update);
    }

    private async void DownloadSelectedButton_Click(object sender, RoutedEventArgs e) {
        List<UpdateEntry> selected = _updateEntries.Where(x => x.SelectCheckBox.IsChecked == true && x.DownloadedPath == null).ToList();
        if (selected.Count == 0) return;

        DownloadSelectedButton.IsEnabled = false;
        InstallSelectedButton.IsEnabled = false;

        for (int i = 0; i < selected.Count; i++) {
            UpdatesStatusText.Text = $"Downloading {i + 1} of {selected.Count}: {selected[i].Update.Title}...";
            await DownloadEntryAsync(selected[i]);
        }

        UpdatesStatusText.Text = $"Downloaded {selected.Count} update(s).";
        UpdateBulkButtonsState();
    }

    private async void InstallSelectedButton_Click(object sender, RoutedEventArgs e) {
        List<UpdateEntry> selected = _updateEntries.Where(x => x.SelectCheckBox.IsChecked == true).ToList();
        if (selected.Count == 0) return;

        bool anyBios = selected.Any(x => IsBiosUpdate(x.Update));
        if (anyBios && BiosUpdatePowerProblem() is string problem) {
            MessageBox.Show(this, $"{problem}\n\nNothing was installed.", "HP Victus Control", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        string names = string.Join("\n", selected.Select(x => "• " + x.Update.Title));
        string warning = anyBios
            ? "\n\nThis includes a BIOS update. Do not turn off, unplug, or close the lid during installation — " +
              "an interrupted BIOS update can leave the laptop unable to boot. Make sure it's plugged into AC power."
            : "\n\nEach installer runs one after another; some may require a restart.";

        MessageBoxResult confirmed = MessageBox.Show(this,
            $"Install {selected.Count} update(s)?\n\n{names}{warning}",
            "HP Victus Control", MessageBoxButton.YesNo, anyBios ? MessageBoxImage.Warning : MessageBoxImage.Question);
        if (confirmed != MessageBoxResult.Yes) return;

        DownloadSelectedButton.IsEnabled = false;
        InstallSelectedButton.IsEnabled = false;

        int succeeded = 0, failed = 0;
        for (int i = 0; i < selected.Count; i++) {
            UpdateEntry entry = selected[i];
            UpdatesStatusText.Text = $"Installing {i + 1} of {selected.Count}: {entry.Update.Title}...";

            if (entry.DownloadedPath == null) await DownloadEntryAsync(entry);
            if (entry.DownloadedPath == null) { failed++; continue; } // download failed — skip to the next one

            // Checked again right before it runs: the charger may have come out during earlier installers.
            if (IsBiosUpdate(entry.Update) && BiosUpdatePowerProblem() is string lateProblem) {
                failed++;
                MessageBox.Show(this, $"Skipped {entry.Update.Title}: {lateProblem}", "HP Victus Control", MessageBoxButton.OK, MessageBoxImage.Warning);
                continue;
            }

            string installerPath = entry.DownloadedPath;
            int? exitCode = await RunInstallerProcessAsync(installerPath);
            if (exitCode == 0) {
                succeeded++;
                InstalledUpdateHistory.MarkInstalled(entry.Update);
                DeleteInstallerIfAutoClean(installerPath);
            } else {
                failed++;
            }
        }

        UpdatesStatusText.Text = failed == 0
            ? $"Installed {succeeded} update(s) successfully."
            : $"Installed {succeeded} update(s); {failed} didn't complete cleanly — check the individual row(s).";
        UpdateBulkButtonsState();
    }

    // Many HP driver installers run completely silently (no visible window) and finish in a
    // few seconds, so without this the app gives no feedback and it looks like nothing happened.
    private async Task<int?> RunInstallerProcessAsync(string filePath) {
        try {
            using Process? process = Process.Start(new ProcessStartInfo(filePath) { UseShellExecute = true });
            if (process == null) return null;
            await process.WaitForExitAsync();
            return process.ExitCode;
        } catch (Exception ex) {
            MessageBox.Show(this, $"Couldn't run {Path.GetFileName(filePath)}: {ex.Message}", "HP Victus Control", MessageBoxButton.OK, MessageBoxImage.Warning);
            return null;
        }
    }

    private static bool IsBiosUpdate(HpDriverUpdate update) => update.Category.Contains("BIOS", StringComparison.OrdinalIgnoreCase);

    // A BIOS flash that loses power part-way can leave the laptop unable to start, so it's only allowed
    // on the charger with enough battery to finish if the charger gets knocked out. Returns why not, or null.
    private const int MinBatteryPercentForBios = 50;

    private static string? BiosUpdatePowerProblem() {
        System.Windows.Forms.PowerStatus power = System.Windows.Forms.SystemInformation.PowerStatus;
        bool pluggedIn = power.PowerLineStatus == System.Windows.Forms.PowerLineStatus.Online;
        // 255% means Windows can't read a battery at all; that's not something to flash a BIOS on either.
        int percent = power.BatteryLifePercent > 1f ? -1 : (int)Math.Round(power.BatteryLifePercent * 100);

        if (!pluggedIn) return "BIOS updates only run on the charger. Plug the laptop in and try again.";
        if (percent < 0) return "Windows can't read the battery level, so the BIOS update was blocked to be safe.";
        if (percent < MinBatteryPercentForBios)
            return $"The battery is at {percent}%. Let it charge to at least {MinBatteryPercentForBios}% before a BIOS update, " +
                   "so it can finish even if the charger gets unplugged.";
        return null;
    }

    private async Task RunInstallerAsync(HpDriverUpdate update, string filePath, Button runButton) {
        bool isBios = IsBiosUpdate(update);
        if (isBios && BiosUpdatePowerProblem() is string problem) {
            MessageBox.Show(this, problem, "HP Victus Control", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        string warning = isBios
            ? "This is a BIOS update. Do not turn off, unplug, or close the lid until it finishes — " +
              "an interrupted BIOS update can leave the laptop unable to boot.\n\nMake sure it's plugged into AC power first."
            : "This will launch the installer, which may require a restart to finish.\n\n" +
              "Many HP driver installers run silently with no visible window — this button will report when it's actually done.";

        MessageBoxResult result = MessageBox.Show(this,
            $"Run \"{update.FileName}\" now?\n\n{warning}",
            "HP Victus Control", MessageBoxButton.YesNo, isBios ? MessageBoxImage.Warning : MessageBoxImage.Question);
        if (result != MessageBoxResult.Yes) return;

        runButton.IsEnabled = false;
        runButton.Content = "Installing...";

        int? exitCode = await RunInstallerProcessAsync(filePath);

        if (exitCode == 0) {
            runButton.Content = "Installed";
            InstalledUpdateHistory.MarkInstalled(update);
            DeleteInstallerIfAutoClean(filePath);
        } else {
            runButton.Content = "Run installer";
            runButton.IsEnabled = true;
            if (exitCode.HasValue) {
                MessageBox.Show(this,
                    $"{update.FileName} exited with code {exitCode.Value}. It may not have fully installed — " +
                    "check the extracted files under C:\\SWSetup for its log, or try running it again.",
                    "HP Victus Control", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
    }

    // ----- System tab: laptop details, battery health, graphics mode ---------------------------

    private bool _systemTabLoaded;
    private List<SystemInfoItem> _systemInfo = new();

    // Filled in the first time the tab is opened: the WMI lookups and the battery report each take
    // about a second, which isn't worth paying at every startup for a page most visits skip.
    private async void LoadSystemTab() {
        if (_systemTabLoaded) return;
        _systemTabLoaded = true;

        LoadGraphicsMode();
        FanTestButton.IsEnabled = _bios.IsAvailable;
        RefreshMaintenanceSizes();

        Task<List<SystemInfoItem>> infoTask = Task.Run(SystemInfo.Collect);
        Task<BatteryHealthReport?> batteryTask = Task.Run(BatteryHealth.TryRead);

        _systemInfo = await infoTask;
        if (_graphicsSwitchSupported.HasValue)
            _systemInfo.Add(new SystemInfoItem("Graphics switch", _graphicsSwitchSupported.Value ? "Yes (BIOS)" : "No"));
        RenderSystemInfo();

        ShowBatteryHealth(await batteryTask);
    }

    private void RenderSystemInfo() {
        SystemInfoGrid.Children.Clear();
        SystemInfoGrid.RowDefinitions.Clear();
        SystemInfoGrid.ColumnDefinitions.Clear();
        SystemInfoGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(140) });
        SystemInfoGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        for (int i = 0; i < _systemInfo.Count; i++) {
            SystemInfoGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var divider = new Border { BorderThickness = new Thickness(0, i == 0 ? 0 : 1, 0, 0) };
            divider.SetResourceReference(Border.BorderBrushProperty, "BorderBrush2");
            Grid.SetRow(divider, i);
            Grid.SetColumnSpan(divider, 2);
            SystemInfoGrid.Children.Add(divider);

            var label = new TextBlock { Text = _systemInfo[i].Label, FontSize = 12, Margin = new Thickness(0, 9, 12, 9) };
            label.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
            Grid.SetRow(label, i);
            SystemInfoGrid.Children.Add(label);

            var value = new TextBlock {
                Text = _systemInfo[i].Value, FontSize = 12, Margin = new Thickness(0, 9, 0, 9), TextWrapping = TextWrapping.Wrap,
                LineHeight = 20
            };
            value.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimaryBrush");
            Grid.SetRow(value, i);
            Grid.SetColumn(value, 1);
            SystemInfoGrid.Children.Add(value);
        }

        SystemInfoStatusText.Text = _systemInfo.Count > 0
            ? "Handy for support chats, warranty checks and forum posts."
            : "Couldn't read the system details.";
        CopySystemInfoButton.IsEnabled = _systemInfo.Count > 0;
    }

    private void CopySystemInfoButton_Click(object sender, RoutedEventArgs e) {
        try {
            System.Windows.Clipboard.SetText(SystemInfo.ToClipboardText(_systemInfo));
            SystemInfoStatusText.Text = "Copied — paste it anywhere.";
        } catch {
            // The clipboard can be briefly locked by another app; a second click works.
            SystemInfoStatusText.Text = "The clipboard was busy — try again.";
        }
    }

    private void CheckWarrantyButton_Click(object sender, RoutedEventArgs e) {
        try {
            Process.Start(new ProcessStartInfo("https://support.hp.com/checkwarranty") { UseShellExecute = true });
        } catch {
            // No default browser configured — nothing sensible to fall back to.
        }
    }

    private void ShowBatteryHealth(BatteryHealthReport? report) {
        if (report == null) {
            BatteryHealthPercentText.Text = "N/A";
            BatteryHealthSummaryText.Text = "Windows didn't return a battery report for this PC.";
            return;
        }

        double health = report.HealthPercent;
        BatteryHealthPercentText.Text = $"{health:0}%";
        BatteryHealthFillColumn.Width = new GridLength(health, GridUnitType.Star);
        BatteryHealthRestColumn.Width = new GridLength(100 - health, GridUnitType.Star);

        string verdict = health >= 90 ? "Healthy — close to new" : health >= 80 ? "Normal wear" : "Worn — expect noticeably shorter battery life";
        BatteryHealthSummaryText.Text = $"{verdict}. Holds {report.FullChargeCapacityMwh / 1000.0:0.0} Wh of the {report.DesignCapacityMwh / 1000.0:0.0} Wh it had when new.";

        var details = new List<string>();
        if (report.CycleCount.HasValue) details.Add($"{report.CycleCount} charge cycles");
        if (report.Chemistry.Length > 0) details.Add(report.Chemistry == "LION" ? "Lithium-ion" : report.Chemistry);
        if (report.HistoryStart.HasValue && report.HistoryStartFullChargeMwh.HasValue)
            details.Add($"{report.HistoryStartFullChargeMwh.Value / 1000.0:0.0} Wh on {report.HistoryStart.Value:MMM d}");
        BatteryHealthDetailText.Text = string.Join("  ·  ", details);

        // The battery's biggest enemy on a gaming laptop is sitting at 100% on the charger all day.
        if (report.PluggedInShare is double pluggedIn && pluggedIn >= 0.7) {
            BatteryHealthAdviceText.Text = $"Plugged in {pluggedIn:P0} of the time recently. Batteries wear fastest when held at 100%: " +
                "if your BIOS setup has HP's Adaptive Battery Optimizer, turning it on lets the laptop hold a lower charge while it stays plugged in.";
            BatteryHealthAdviceText.Visibility = Visibility.Visible;
        }
    }

    // null until the BIOS has been asked; the switch is only offered when the BIOS reports one.
    private bool? _graphicsSwitchSupported;
    private HpGpuMode? _currentGpuMode;
    private bool _syncingGpuModeRadios;

    private void LoadGraphicsMode() {
        if (!_bios.IsAvailable) {
            GraphicsModeStatusText.Text = "Needs the HP BIOS interface, which isn't available right now.";
            return;
        }

        try {
            _graphicsSwitchSupported = _bios.IsGraphicsSwitchSupported();
        } catch (HpBiosException) {
            GraphicsModeStatusText.Text = "The BIOS didn't answer the graphics switch query.";
            return;
        }

        if (_graphicsSwitchSupported != true) {
            GraphicsModeStatusText.Text =
                "This laptop doesn't have a graphics switch: its BIOS reports no MUX. The Intel GPU always drives the built-in " +
                "screen and the NVIDIA GPU renders games through it. That's wired into the hardware, so no app or setting can change it — " +
                "\"Use NVIDIA GPU\" on each game is already the fastest option available.";
            return;
        }

        try {
            _currentGpuMode = _bios.GetGpuMode();
        } catch (HpBiosException) {
            GraphicsModeStatusText.Text = "This laptop has a graphics switch, but the BIOS didn't report its current mode.";
            return;
        }

        GraphicsModeStatusText.Text =
            "Hybrid lets the Intel GPU run the screen and hands game frames over from NVIDIA — better battery life. " +
            "NVIDIA only connects the screen straight to the NVIDIA GPU — more fps, but the battery drains faster. Takes effect after a restart.";
        GraphicsModeTrack.Visibility = Visibility.Visible;
        SyncGpuModeRadios(_currentGpuMode.Value);
    }

    private void SyncGpuModeRadios(HpGpuMode mode) {
        _syncingGpuModeRadios = true;
        HybridGpuRadio.IsChecked = mode != HpGpuMode.Discrete;
        DiscreteGpuRadio.IsChecked = mode == HpGpuMode.Discrete;
        _syncingGpuModeRadios = false;
    }

    private void GpuModeRadio_Checked(object sender, RoutedEventArgs e) {
        if (_syncingGpuModeRadios || _graphicsSwitchSupported != true || _currentGpuMode == null) return;

        HpGpuMode wanted = ReferenceEquals(sender, DiscreteGpuRadio) ? HpGpuMode.Discrete : HpGpuMode.Hybrid;
        if (wanted == _currentGpuMode || (wanted == HpGpuMode.Hybrid && _currentGpuMode == HpGpuMode.Optimus)) return;

        string name = wanted == HpGpuMode.Discrete ? "NVIDIA only" : "Hybrid";
        MessageBoxResult confirmed = MessageBox.Show(this,
            $"Switch graphics mode to {name}?\n\nThe BIOS applies this the next time the laptop restarts." +
            (wanted == HpGpuMode.Discrete ? "\n\nNVIDIA only drains the battery noticeably faster." : ""),
            "HP Victus Control", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (confirmed != MessageBoxResult.Yes) {
            SyncGpuModeRadios(_currentGpuMode.Value);
            return;
        }

        try {
            _bios.SetGpuMode(wanted);
            _currentGpuMode = wanted;
            GraphicsModeHintText.Text = $"{name} is set and will be used after you restart.";
            GraphicsModeHintText.Visibility = Visibility.Visible;
            RestartNowButton.Visibility = Visibility.Visible;
        } catch (HpBiosException ex) {
            SyncGpuModeRadios(_currentGpuMode.Value);
            MessageBox.Show(this, $"The BIOS didn't accept the change: {ex.Message}", "HP Victus Control", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void RestartNowButton_Click(object sender, RoutedEventArgs e) {
        MessageBoxResult confirmed = MessageBox.Show(this,
            "Restart the laptop now? Save anything you have open first.",
            "HP Victus Control", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (confirmed != MessageBoxResult.Yes) return;

        try {
            Process.Start(new ProcessStartInfo("shutdown.exe", "/r /t 0") { UseShellExecute = false, CreateNoWindow = true });
        } catch (Exception ex) {
            MessageBox.Show(this, $"Couldn't restart: {ex.Message}", "HP Victus Control", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    // ----- Maintenance: fan test, shader cache, driver downloads ------------------------------

    private async void RefreshMaintenanceSizes() {
        (FolderUsage shaders, FolderUsage downloads) = await Task.Run(() => (Maintenance.MeasureShaderCaches(), Maintenance.MeasureDriverDownloads()));

        ShaderCacheSizeText.Text = shaders.Files == 0
            ? "Empty. Games rebuild it as they run."
            : $"{shaders} of compiled NVIDIA, DirectX and Intel shaders. Clearing it can fix stutter after a driver update; games rebuild it, so their next launch loads a little slower.";
        ClearShaderCacheButton.IsEnabled = shaders.Files > 0;

        DriverDownloadsSizeText.Text = downloads.Files == 0
            ? "No installers kept."
            : $"{downloads} in {downloads.Files} installer{(downloads.Files == 1 ? "" : "s")} downloaded from the Drivers tab.";
        CleanDriverDownloadsButton.IsEnabled = downloads.Files > 0;
    }

    private async void ClearShaderCacheButton_Click(object sender, RoutedEventArgs e) {
        ClearShaderCacheButton.IsEnabled = false;
        ShaderCacheSizeText.Text = "Clearing…";
        (FolderUsage freed, int skipped) = await Task.Run(Maintenance.ClearShaderCaches);
        RefreshMaintenanceSizes();
        _tray.ShowBalloon("HP Victus Control", $"Shader cache cleared — freed {freed}" +
            (skipped > 0 ? $" ({skipped} file{(skipped == 1 ? "" : "s")} in use by a running game were left)" : ""));
    }

    private async void CleanDriverDownloadsButton_Click(object sender, RoutedEventArgs e) {
        MessageBoxResult confirmed = MessageBox.Show(this,
            "Delete every downloaded driver and BIOS installer? You can download them again from the Drivers tab.",
            "HP Victus Control", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (confirmed != MessageBoxResult.Yes) return;

        CleanDriverDownloadsButton.IsEnabled = false;
        (FolderUsage freed, int skipped) = await Task.Run(() => Maintenance.CleanDriverDownloads());
        RefreshMaintenanceSizes();
        _tray.ShowBalloon("HP Victus Control", $"Freed {freed}" + (skipped > 0 ? $"; {skipped} installer(s) still in use were left" : ""));
    }

    private void AutoCleanDownloadsCheckBox_Changed(object sender, RoutedEventArgs e) {
        if (_initializing) return;
        _settings.AutoCleanDriverDownloads = AutoCleanDownloadsCheckBox.IsChecked == true;
        _settings.Save();
    }

    // Once an installer has run successfully there's no reason to keep a copy of it.
    private void DeleteInstallerIfAutoClean(string path) {
        if (_settings.AutoCleanDriverDownloads && Maintenance.DeleteDownloadedInstaller(path) && _systemTabLoaded) RefreshMaintenanceSizes();
    }

    private async void SweepOldDriverDownloads() {
        if (!_settings.AutoCleanDriverDownloads) return;
        await Task.Run(() => Maintenance.CleanDriverDownloads(TimeSpan.FromDays(30)));
    }

    private bool _fanTestRunning;

    // Full speed for 15 seconds, sampling each fan every second. A healthy fan on this laptop reaches
    // roughly 5,000 RPM within a few seconds; one that stays low, or lags well behind the other fan,
    // is worth a closer look (dust, a worn bearing, or a loose connector).
    private async void FanTestButton_Click(object sender, RoutedEventArgs e) {
        if (!_bios.IsAvailable || _fanTestRunning) return;

        _fanTestRunning = true;
        FanTestButton.IsEnabled = false;
        FanTestResultText.Visibility = Visibility.Visible;

        bool maxFanWasOn = false;
        try {
            maxFanWasOn = _bios.GetMaxFanSpeed();
            (byte cpuStart, byte gpuStart) = _bios.GetFanLevels();
            byte cpuPeak = cpuStart, gpuPeak = gpuStart;
            int? cpuReached = null, gpuReached = null;

            _bios.SetMaxFanSpeed(true);
            for (int second = 1; second <= 15; second++) {
                FanTestButton.Content = $"Testing… {16 - second}s";
                FanTestResultText.Text = $"CPU fan ~{cpuPeak * 100:N0} RPM  ·  GPU fan ~{gpuPeak * 100:N0} RPM";
                await Task.Delay(1000);
                try {
                    (byte cpu, byte gpu) = _bios.GetFanLevels();
                    cpuPeak = Math.Max(cpuPeak, cpu);
                    gpuPeak = Math.Max(gpuPeak, gpu);
                    if (cpuReached == null && cpu * 100 >= 4000) cpuReached = second;
                    if (gpuReached == null && gpu * 100 >= 4000) gpuReached = second;
                } catch (HpBiosException) {
                    // A missed reading under load; the next second tries again.
                }
            }

            FanTestResultText.Text = string.Join(Environment.NewLine,
                DescribeFanResult("CPU fan", cpuStart, cpuPeak, cpuReached, gpuPeak),
                DescribeFanResult("GPU fan", gpuStart, gpuPeak, gpuReached, cpuPeak));
        } catch (HpBiosException ex) {
            FanTestResultText.Text = $"The BIOS didn't answer during the test: {ex.Message}";
        } finally {
            try {
                if (!maxFanWasOn) _bios.SetMaxFanSpeed(false);
            } catch (HpBiosException) {
                // Leave it; the max fan switch in Fan control still works to turn it off.
            }
            // Let the auto fan curve re-apply its own levels on the next tick.
            _lastAutoCpuFanLevel = null;
            _lastAutoGpuFanLevel = null;
            _fanTestRunning = false;
            FanTestButton.Content = "Test fans";
            FanTestButton.IsEnabled = true;
        }
    }

    private static string DescribeFanResult(string name, byte start, byte peak, int? reachedAt, byte otherPeak) {
        int startRpm = start * 100, peakRpm = peak * 100;
        string numbers = $"{name}: ~{startRpm:N0} → ~{peakRpm:N0} RPM";
        if (peakRpm < 4000) return $"{numbers}  ✗  didn't reach full speed — worth checking for dust or a failing fan.";
        if (otherPeak > 0 && peak < otherPeak * 0.8) return $"{numbers}  ⚠  noticeably slower than the other fan.";
        return $"{numbers}  ✓  healthy{(reachedAt.HasValue ? $", full speed within {reachedAt} s" : "")}.";
    }

    private void TuneWifiCheckBox_Changed(object sender, RoutedEventArgs e) {
        if (_initializing) return;
        _settings.TuneWifiForGames = TuneWifiCheckBox.IsChecked == true;
        _settings.Save();

        // Takes effect straight away for a game that's already running.
        if (!_settings.TuneWifiForGames) _wifiTuning.Stop();
        else if (_activeGameProfileExeName != null) _wifiTuning.Start();
    }

    // ----- Per-game profiles ----------------------------------------------------------------

    // At most one tracked game "owns" the current mode at a time; when it exits, the mode from
    // just before it launched is restored.
    private string? _activeGameProfileExeName;
    private HpFanMode _preGameMode;
    private bool _activeGameMaxFan;
    private bool _preGameMaxFan;

    private readonly GameBoost _gameBoost = new();
    private readonly WifiTuning _wifiTuning = new();
    // The resolution from before a game lowered it; null when no game changed it.
    private (int Width, int Height)? _preGameResolution;

    private sealed record TrackedGame(string ExeName, string Name, HpFanMode? Mode, int RefreshRateHz,
        bool MaxFan, bool Boost, (int Width, int Height)? Resolution);

    private void CheckGameProfiles() {
        var tracked = new List<TrackedGame>();
        foreach (GameProfile profile in _settings.GameProfiles) {
            HpFanMode? mode = Enum.TryParse(profile.Mode, out HpFanMode parsed) ? parsed : null;
            int hz = _refreshRates.Contains(profile.RefreshRateHz) ? profile.RefreshRateHz : 0;
            (int, int)? resolution = profile.ResolutionWidth > 0 && profile.ResolutionHeight > 0
                ? (profile.ResolutionWidth, profile.ResolutionHeight) : null;
            // With Wi-Fi tuning on, every game in the list counts, even one with nothing else set.
            if (mode == null && hz == 0 && !profile.MaxFan && !profile.Boost && resolution == null && !_settings.TuneWifiForGames) continue;

            string exeName = Path.GetFileNameWithoutExtension(profile.ExecutablePath);
            if (exeName.Length > 0) tracked.Add(new TrackedGame(exeName, profile.Name, mode, hz, profile.MaxFan, profile.Boost, resolution));
        }
        if (tracked.Count == 0 && _activeGameProfileExeName == null) return;

        HashSet<string> running = GetRunningProcessNames();

        if (_activeGameProfileExeName != null) {
            if (IsGameRunning(running, _activeGameProfileExeName)) {
                KeepMaxFanAsserted();
                KeepGameBoosted(_activeGameProfileExeName);
                return;
            }

            string endedGame = _activeGameProfileExeName;
            // Resolution first: re-applying the mode below writes its refresh rate at whatever size is current.
            RestorePreGameResolution();
            // Re-applying the earlier mode also puts back that mode's refresh rate.
            ApplyMode(_preGameMode);
            if (_activeGameMaxFan) {
                _activeGameMaxFan = false;
                MaxFanCheckBox.IsChecked = _preGameMaxFan;
            }
            _gameBoost.Stop();
            _wifiTuning.Stop();
            _activeGameProfileExeName = null;
            _tray.ShowBalloon("HP Victus Control", $"{endedGame} closed — restored {_preGameMode} mode");
        }

        foreach (TrackedGame game in tracked) {
            if (!IsGameRunning(running, game.ExeName)) continue;

            _preGameMode = _currentMode;
            if (game.Mode.HasValue) ApplyMode(game.Mode.Value);
            if (game.RefreshRateHz > 0) ApplyRefreshRate(game.RefreshRateHz);
            // After the mode and refresh rate, which are saved to the registry: the lowered resolution isn't,
            // so it can't outlive the game.
            bool resolutionChanged = game.Resolution.HasValue && ApplyGameResolution(game.Resolution.Value);
            if (game.MaxFan) {
                _preGameMaxFan = MaxFanCheckBox.IsChecked == true;
                _activeGameMaxFan = true;
                // The checkbox's own handler is what talks to the BIOS and the tray menu.
                MaxFanCheckBox.IsChecked = true;
                KeepMaxFanAsserted();
            }
            _activeGameProfileExeName = game.ExeName;
            if (game.Boost) {
                _gameBoost.Start();
                KeepGameBoosted(game.ExeName);
            }
            bool wifiTuned = _settings.TuneWifiForGames && _wifiTuning.Start() > 0;

            var changes = new List<string>();
            if (game.Mode.HasValue) changes.Add($"{game.Mode.Value} mode");
            if (game.RefreshRateHz > 0) changes.Add($"{game.RefreshRateHz}Hz");
            if (resolutionChanged) changes.Add($"{game.Resolution!.Value.Width}×{game.Resolution.Value.Height}");
            if (game.MaxFan) changes.Add("max fan");
            if (game.Boost) changes.Add("game boost");
            if (wifiTuned) changes.Add("Wi-Fi tuning");
            if (changes.Count > 0)
                _tray.ShowBalloon("HP Victus Control", $"{game.Name} detected — switched to {string.Join(", ", changes)}");
            break;
        }
    }

    private bool ApplyGameResolution((int Width, int Height) resolution) {
        (int Width, int Height)? current = DisplayRefreshRate.GetCurrentResolution();
        if (current == null || current.Value == resolution) return false;

        if (!DisplayRefreshRate.SetResolution(resolution.Width, resolution.Height)) return false;
        _preGameResolution ??= current;
        return true;
    }

    private void RestorePreGameResolution() {
        if (_preGameResolution is not (int width, int height)) return;
        _preGameResolution = null;
        DisplayRefreshRate.SetResolution(width, height, persist: true);
    }

    // Games don't always run under the file name that was added: GTA V Enhanced's GTA5.exe starts the
    // actual game as "GTA5_Enhanced", so an exact name match never saw it running. A running process
    // also counts when its name is the game's name plus a "_", "-" or " " suffix — close enough to
    // catch those renamed game processes, strict enough that "Game" doesn't match "GameBar".
    private static bool IsGameRunning(HashSet<string> running, string exeName) =>
        running.Contains(exeName) || running.Any(name => IsGameProcessName(name, exeName));

    private static bool IsGameProcessName(string processName, string exeName) {
        if (processName.Equals(exeName, StringComparison.OrdinalIgnoreCase)) return true;
        return exeName.Length >= 3
            && processName.Length > exeName.Length
            && processName.StartsWith(exeName, StringComparison.OrdinalIgnoreCase)
            && processName[exeName.Length] is '_' or '-' or ' ';
    }

    private void KeepGameBoosted(string exeName) {
        if (!_gameBoost.IsActive) return;

        var processes = new List<Process>();
        foreach (Process process in Process.GetProcesses()) {
            if (IsGameProcessName(process.ProcessName, exeName)) processes.Add(process);
            else process.Dispose();
        }
        _gameBoost.BoostProcesses(processes);
        foreach (Process process in processes) process.Dispose();
    }

    // The BIOS lets its own curve take the fans back over after a while — and whenever the
    // performance mode changes — so while a max-fan game is running the request is re-sent as
    // soon as the BIOS reports it off. Turning the switch off by hand stops that.
    private void KeepMaxFanAsserted() {
        if (!_activeGameMaxFan || MaxFanCheckBox.IsChecked != true) return;

        try {
            if (!_bios.GetMaxFanSpeed()) _bios.SetMaxFanSpeed(true);
        } catch (HpBiosException) {
            // Transient BIOS errors are normal under load; the next tick tries again.
        }
    }

    private static HashSet<string> GetRunningProcessNames() {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Process process in Process.GetProcesses()) {
            names.Add(process.ProcessName);
            process.Dispose();
        }
        return names;
    }

    private readonly Dictionary<string, ImageSource?> _gameIconCache = new(StringComparer.OrdinalIgnoreCase);

    // FPS caps live in the NVIDIA driver's own profile database rather than in settings.json.
    // Opening a driver session costs ~200ms, so the list never waits for one: it renders with
    // whatever's already known and redraws once the driver answers.
    private Dictionary<string, int> _frameLimits = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _frameLimitsKnownFor = new(StringComparer.OrdinalIgnoreCase);
    private bool _frameLimitsLoading;

    private async void LoadFrameLimits() {
        if (_frameLimitsLoading || !NvidiaFrameLimiter.IsAvailable) return;

        List<string> paths = _settings.GameProfiles.Select(p => p.ExecutablePath).ToList();
        if (paths.All(_frameLimitsKnownFor.Contains)) return;

        _frameLimitsLoading = true;
        Dictionary<string, int> limits = await Task.Run(() => NvidiaFrameLimiter.GetLimits(paths));
        _frameLimitsLoading = false;

        foreach (string path in paths) _frameLimitsKnownFor.Add(path);
        foreach ((string path, int fps) in limits) _frameLimits[path] = fps;
        RenderGameProfilesList();
    }

    private void RenderGameProfilesList() {
        LoadFrameLimits();

        GameProfilesListPanel.Children.Clear();
        NoGameProfilesText.Visibility = _settings.GameProfiles.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        GameCountText.Text = _settings.GameProfiles.Count switch {
            0 => "",
            1 => "1 game",
            int count => $"{count} games"
        };

        foreach (GameProfile profile in _settings.GameProfiles.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase))
            GameProfilesListPanel.Children.Add(BuildGameCard(profile));
    }

    // Two cards per row fit comfortably from about 760px; narrower than that they get one each.
    private void GameProfilesListPanel_SizeChanged(object sender, SizeChangedEventArgs e) {
        int columns = e.NewSize.Width >= 760 ? 2 : 1;
        if (GameProfilesListPanel.Columns != columns) GameProfilesListPanel.Columns = columns;
    }

    private FrameworkElement BuildGameCard(GameProfile profile) {
        bool installed = File.Exists(profile.ExecutablePath);
        var body = new StackPanel();

        // Header: icon, name, where it's installed, and Remove for manually added games.
        var header = new Grid { Margin = new Thickness(0, 0, 0, 4) };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        FrameworkElement icon = BuildAppIcon(profile.ExecutablePath, profile.Name);
        header.Children.Add(icon);

        var title = new StackPanel { Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        var nameText = new TextBlock { Text = profile.Name, FontWeight = FontWeights.SemiBold, FontSize = 13, TextTrimming = TextTrimming.CharacterEllipsis };
        nameText.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimaryBrush");
        title.Children.Add(nameText);
        var sourceText = new TextBlock {
            Text = installed ? DescribeGameSource(profile.ExecutablePath) : "Not found on this PC",
            FontSize = 11, Margin = new Thickness(0, 2, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis,
            ToolTip = profile.ExecutablePath
        };
        sourceText.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
        title.Children.Add(sourceText);
        if (installed) ShowGameInstallSize(profile.ExecutablePath, sourceText);
        Grid.SetColumn(title, 1);
        header.Children.Add(title);

        // Scanned games reappear on the next scan, so only manually added ones get a Remove button.
        if (!profile.IsScanned) {
            var removeButton = new Button {
                Content = "Remove", Style = (Style)FindResource("SecondaryButton"),
                Padding = new Thickness(12, 6, 12, 6), Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center
            };
            removeButton.Click += (_, _) => {
                _settings.GameProfiles.Remove(profile);
                _settings.Save();
                RenderGameProfilesList();
            };
            Grid.SetColumn(removeButton, 2);
            header.Children.Add(removeButton);
        }
        body.Children.Add(header);

        body.Children.Add(BuildLabeledRow("Mode", null, BuildGameModeSelector(profile)));
        if (_refreshRates.Count > 1) body.Children.Add(BuildLabeledRow("Refresh rate", null, BuildGameRefreshRateSelector(profile)));
        body.Children.Add(BuildLabeledRow("Resolution", "Empty = no change", BuildGameResolutionSelector(profile)));
        if (NvidiaFrameLimiter.IsAvailable && installed)
            body.Children.Add(BuildLabeledRow("FPS cap", "Empty = no cap", BuildGameFrameLimitSelector(profile)));

        if (NvidiaGpuSensor.IsAvailable && installed) {
            var gpuToggle = new CheckBox {
                Content = "Use NVIDIA GPU", Style = (Style)FindResource("GlowToggle"), Margin = new Thickness(0, 12, 0, 0),
                IsChecked = GpuPreference.IsHighPerformance(profile.ExecutablePath)
            };
            gpuToggle.Checked += (_, _) => SetGameGpuPreference(profile.ExecutablePath, true);
            gpuToggle.Unchecked += (_, _) => SetGameGpuPreference(profile.ExecutablePath, false);
            body.Children.Add(gpuToggle);
        }

        var maxFanToggle = new CheckBox {
            Content = "Max fan while playing", Style = (Style)FindResource("GlowToggle"), Margin = new Thickness(0, 10, 0, 0),
            ToolTip = "Runs both fans flat out while this game is open, then puts the fans back afterwards",
            IsChecked = profile.MaxFan
        };
        maxFanToggle.Checked += (_, _) => SaveGameMaxFan(profile, true);
        maxFanToggle.Unchecked += (_, _) => SaveGameMaxFan(profile, false);
        body.Children.Add(maxFanToggle);

        var boostToggle = new CheckBox {
            Content = "Game boost", Style = (Style)FindResource("GlowToggle"), Margin = new Thickness(0, 10, 0, 0),
            ToolTip = "While this game runs: gives it high CPU priority and closes OneDrive so syncing doesn't compete. Both go back when the game closes.",
            IsChecked = profile.Boost
        };
        boostToggle.Checked += (_, _) => SaveGameBoost(profile, true);
        boostToggle.Unchecked += (_, _) => SaveGameBoost(profile, false);
        body.Children.Add(boostToggle);

        return new Border { Style = (Style)FindResource("Card"), Margin = new Thickness(7, 0, 7, 14), Child = body };
    }

    // Install sizes are cached for the session: Steam's is instant, but other games are measured by
    // walking their whole folder, which can take a few seconds for a 100 GB install.
    private readonly Dictionary<string, long?> _gameInstallSizes = new(StringComparer.OrdinalIgnoreCase);

    private async void ShowGameInstallSize(string exePath, TextBlock sourceText) {
        string source = sourceText.Text;
        if (!_gameInstallSizes.TryGetValue(exePath, out long? bytes)) {
            bytes = await Task.Run(() => GameInstallSize.TryGet(exePath));
            _gameInstallSizes[exePath] = bytes;
        }
        if (bytes.HasValue) sourceText.Text = $"{source}  ·  {Maintenance.FormatBytes(bytes.Value)}";
    }

    // Any size the display reports, typed in, rather than a handful of presets: two number boxes and
    // Apply, empty for no change. The resolution switches while the game runs and goes back after.
    private FrameworkElement BuildGameResolutionSelector(GameProfile profile) {
        bool set = profile.ResolutionWidth > 0 && profile.ResolutionHeight > 0;
        string tip = "Leave both empty to keep the desktop resolution while this game runs";

        TextBox widthBox = BuildNumberBox(set ? profile.ResolutionWidth.ToString() : "", 52, tip);
        TextBox heightBox = BuildNumberBox(set ? profile.ResolutionHeight.ToString() : "", 52, tip);

        var times = new TextBlock {
            Text = "×", FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 6, 0)
        };
        times.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");

        var apply = new Button {
            Content = "Apply", Style = (Style)FindResource("SecondaryButton"),
            Padding = new Thickness(12, 6, 12, 6), Margin = new Thickness(8, 0, 0, 0)
        };
        apply.Click += (_, _) => ApplyTypedResolution(profile, widthBox, heightBox);
        foreach (TextBox box in new[] { widthBox, heightBox }) {
            box.KeyDown += (_, e) => {
                if (e.Key != Key.Enter) return;
                e.Handled = true;
                ApplyTypedResolution(profile, widthBox, heightBox);
            };
        }

        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(WrapInputBox(widthBox));
        row.Children.Add(times);
        row.Children.Add(WrapInputBox(heightBox));
        row.Children.Add(apply);
        return row;
    }

    private TextBox BuildNumberBox(string text, double width, string toolTip) {
        var box = new TextBox {
            Width = width, Text = text, MaxLength = 5,
            BorderThickness = new Thickness(0), Background = System.Windows.Media.Brushes.Transparent,
            FontSize = 12, TextAlignment = TextAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center,
            ToolTip = toolTip
        };
        box.SetResourceReference(System.Windows.Controls.Control.ForegroundProperty, "TextPrimaryBrush");
        box.SetResourceReference(System.Windows.Controls.Primitives.TextBoxBase.CaretBrushProperty, "TextPrimaryBrush");
        return box;
    }

    private Border WrapInputBox(TextBox box) {
        var border = new Border {
            CornerRadius = new CornerRadius(9), Padding = new Thickness(8, 5, 8, 5),
            VerticalAlignment = VerticalAlignment.Center, Child = box
        };
        border.SetResourceReference(Border.BackgroundProperty, "TrackBrush");
        return border;
    }

    private void ApplyTypedResolution(GameProfile profile, TextBox widthBox, TextBox heightBox) {
        string typedWidth = widthBox.Text.Trim(), typedHeight = heightBox.Text.Trim();

        if (typedWidth.Length == 0 && typedHeight.Length == 0) {
            SaveGameResolution(profile, 0, 0);
            return;
        }

        if (!int.TryParse(typedWidth, out int width) || !int.TryParse(typedHeight, out int height) || width <= 0 || height <= 0) {
            MessageBox.Show(this, "Enter a width and a height, for example 1280 × 720 — or clear both boxes to leave the resolution alone.",
                "HP Victus Control", MessageBoxButton.OK, MessageBoxImage.Warning);
            RestoreResolutionBoxes(profile, widthBox, heightBox);
            return;
        }

        // Windows only switches to modes the display reports, so a typed size that isn't one of them
        // would silently do nothing when the game starts.
        if (!DisplayRefreshRate.IsResolutionSupported(width, height)) {
            string supported = string.Join("\n", DisplayRefreshRate.GetAllResolutions().Take(12).Select(m => $"• {m.Width} × {m.Height}"));
            MessageBox.Show(this,
                $"This display has no {width} × {height} mode, so Windows would refuse to switch to it.\n\nSizes it does have:\n{supported}",
                "HP Victus Control", MessageBoxButton.OK, MessageBoxImage.Warning);
            RestoreResolutionBoxes(profile, widthBox, heightBox);
            return;
        }

        SaveGameResolution(profile, width, height);
    }

    private static void RestoreResolutionBoxes(GameProfile profile, TextBox widthBox, TextBox heightBox) {
        bool set = profile.ResolutionWidth > 0 && profile.ResolutionHeight > 0;
        widthBox.Text = set ? profile.ResolutionWidth.ToString() : "";
        heightBox.Text = set ? profile.ResolutionHeight.ToString() : "";
    }

    private void SaveGameResolution(GameProfile profile, int width, int height) {
        profile.ResolutionWidth = width;
        profile.ResolutionHeight = height;
        _settings.Save();
    }

    // A label on the left (with an optional hint under it) and its control pushed to the right.
    private FrameworkElement BuildLabeledRow(string label, string? hint, FrameworkElement control) {
        var row = new Grid { Margin = new Thickness(0, 12, 0, 0) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var labels = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) };
        var labelText = new TextBlock { Text = label, FontSize = 12 };
        labelText.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimaryBrush");
        labels.Children.Add(labelText);
        if (hint != null) {
            var hintText = new TextBlock { Text = hint, FontSize = 10, Margin = new Thickness(0, 2, 0, 0) };
            hintText.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
            labels.Children.Add(hintText);
        }
        row.Children.Add(labels);

        control.HorizontalAlignment = System.Windows.HorizontalAlignment.Right;
        control.VerticalAlignment = VerticalAlignment.Center;
        control.Margin = new Thickness(0);
        Grid.SetColumn(control, 1);
        row.Children.Add(control);
        return row;
    }

    // The full path lives in the tooltip; the card itself just says where the game came from.
    private static string DescribeGameSource(string exePath) {
        if (exePath.Contains(@"\steamapps\", StringComparison.OrdinalIgnoreCase)) return "Steam";
        if (exePath.Contains(@"\Epic Games\", StringComparison.OrdinalIgnoreCase)) return "Epic Games";
        if (exePath.Contains(@"\XboxGames\", StringComparison.OrdinalIgnoreCase)) return "Xbox";
        string? folder = Path.GetFileName(Path.GetDirectoryName(exePath));
        return string.IsNullOrEmpty(folder) ? exePath : folder;
    }

    private FrameworkElement BuildGameModeSelector(GameProfile profile) {
        (string Label, string Value)[] modes = {
            // Stored as "None" (don't switch), which leaves the mode chosen on the Performance page in charge.
            ("Default", GameProfile.NoSwitch),
            ("Balanced", nameof(HpFanMode.Balanced)),
            ("Performance", nameof(HpFanMode.Performance)),
            ("Cool", nameof(HpFanMode.Cool)),
        };

        return BuildSegmentTrack(modes.Select(m => (
            m.Label,
            m.Value == GameProfile.NoSwitch ? "Use the mode selected on the Performance page" : (string?)null,
            string.Equals(profile.Mode, m.Value, StringComparison.OrdinalIgnoreCase),
            (Action)(() => {
                profile.Mode = m.Value;
                _settings.Save();
            }))));
    }

    private FrameworkElement BuildGameRefreshRateSelector(GameProfile profile) {
        bool fixedRate = _refreshRates.Contains(profile.RefreshRateHz);
        var options = new List<(string, string?, bool, Action)> {
            ("Auto", "Keep the refresh rate that goes with the mode", !fixedRate, () => SaveGameRefreshRate(profile, 0))
        };
        foreach (int rate in _refreshRates)
            options.Add(($"{rate}Hz", null, fixedRate && rate == profile.RefreshRateHz, () => SaveGameRefreshRate(profile, rate)));

        return BuildSegmentTrack(options);
    }

    private void SaveGameRefreshRate(GameProfile profile, int hz) {
        profile.RefreshRateHz = hz;
        _settings.Save();
    }

    private void SaveGameMaxFan(GameProfile profile, bool maxFan) {
        profile.MaxFan = maxFan;
        _settings.Save();

        // Turning it off for the game that's currently driving the fans takes effect right away.
        if (!maxFan && _activeGameMaxFan
            && string.Equals(_activeGameProfileExeName, Path.GetFileNameWithoutExtension(profile.ExecutablePath), StringComparison.OrdinalIgnoreCase)) {
            _activeGameMaxFan = false;
            MaxFanCheckBox.IsChecked = _preGameMaxFan;
        }
    }

    private void SaveGameBoost(GameProfile profile, bool boost) {
        profile.Boost = boost;
        _settings.Save();

        // Switching it for the game that's running right now takes effect immediately.
        string exeName = Path.GetFileNameWithoutExtension(profile.ExecutablePath);
        if (!string.Equals(_activeGameProfileExeName, exeName, StringComparison.OrdinalIgnoreCase)) return;
        if (boost) {
            _gameBoost.Start();
            KeepGameBoosted(exeName);
        } else {
            _gameBoost.Stop();
        }
    }

    // The driver's frame limiter takes any rate from 20 to 1000 fps, so this is a plain number
    // box rather than a list of presets: type a cap, or leave it empty for no cap at all.
    private FrameworkElement BuildGameFrameLimitSelector(GameProfile profile) {
        _frameLimits.TryGetValue(profile.ExecutablePath, out int current);

        TextBox input = BuildNumberBox(current > 0 ? current.ToString() : "", 46,
            $"{NvidiaFrameLimiter.MinFps}–{NvidiaFrameLimiter.MaxFps} fps, or empty for no cap");
        input.MaxLength = 4;
        Border inputBox = WrapInputBox(input);

        var apply = new Button {
            Content = "Apply", Style = (Style)FindResource("SecondaryButton"),
            Padding = new Thickness(12, 6, 12, 6), Margin = new Thickness(8, 0, 0, 0)
        };
        apply.Click += (_, _) => ApplyTypedFrameLimit(profile, input);
        input.KeyDown += (_, e) => {
            if (e.Key != Key.Enter) return;
            e.Handled = true;
            ApplyTypedFrameLimit(profile, input);
        };

        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(inputBox);
        row.Children.Add(apply);
        return row;
    }

    private async void ApplyTypedFrameLimit(GameProfile profile, TextBox input) {
        string typed = input.Text.Trim();
        int fps = 0;

        if (typed.Length > 0 && (!int.TryParse(typed, out fps) || fps < NvidiaFrameLimiter.MinFps || fps > NvidiaFrameLimiter.MaxFps)) {
            MessageBox.Show(this,
                $"Enter a frame rate between {NvidiaFrameLimiter.MinFps} and {NvidiaFrameLimiter.MaxFps}, or leave the box empty for no cap.",
                "HP Victus Control", MessageBoxButton.OK, MessageBoxImage.Warning);
            _frameLimits.TryGetValue(profile.ExecutablePath, out int previous);
            input.Text = previous > 0 ? previous.ToString() : "";
            return;
        }

        string path = profile.ExecutablePath;
        input.IsEnabled = false;
        // Writing the driver's profile database takes long enough to be felt on the UI thread.
        bool applied = await Task.Run(() => NvidiaFrameLimiter.SetLimit(path, fps));
        input.IsEnabled = true;

        if (!applied) {
            MessageBox.Show(this, "The NVIDIA driver didn't accept that frame rate limit.",
                "HP Victus Control", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (fps > 0) _frameLimits[path] = fps;
        else _frameLimits.Remove(path);
        input.Text = fps > 0 ? fps.ToString() : "";
    }

    private FrameworkElement BuildSegmentTrack(IEnumerable<(string Label, string? ToolTip, bool IsSelected, Action OnSelected)> options) {
        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        string group = $"Segment_{Guid.NewGuid():N}";

        foreach ((string label, string? toolTip, bool isSelected, Action onSelected) in options) {
            var radio = new RadioButton {
                Content = label, GroupName = group, Style = (Style)FindResource("SegmentRadio"),
                Padding = new Thickness(10, 5, 10, 5), IsChecked = isSelected, ToolTip = toolTip
            };
            radio.Checked += (_, _) => onSelected();
            panel.Children.Add(radio);
        }

        var track = new Border {
            CornerRadius = new CornerRadius(9), HorizontalAlignment = System.Windows.HorizontalAlignment.Left, Child = panel
        };
        track.SetResourceReference(Border.BackgroundProperty, "TrackBrush");
        return track;
    }

    private FrameworkElement BuildAppIcon(string exePath, string name) {
        ImageSource? source = GetExecutableIcon(exePath);
        if (source != null) {
            return new System.Windows.Controls.Image {
                Source = source, Width = 32, Height = 32, VerticalAlignment = VerticalAlignment.Top
            };
        }

        var letter = new TextBlock {
            Text = name.Length > 0 ? name[..1].ToUpperInvariant() : "?",
            FontWeight = FontWeights.Bold, FontSize = 14,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
        };
        letter.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");

        var tile = new Border {
            Width = 32, Height = 32, CornerRadius = new CornerRadius(8), VerticalAlignment = VerticalAlignment.Top, Child = letter
        };
        tile.SetResourceReference(Border.BackgroundProperty, "TrackBrush");
        return tile;
    }

    private ImageSource? GetExecutableIcon(string exePath) {
        if (_gameIconCache.TryGetValue(exePath, out ImageSource? cached)) return cached;

        ImageSource? source = null;
        try {
            if (File.Exists(exePath)) {
                using System.Drawing.Icon? icon = System.Drawing.Icon.ExtractAssociatedIcon(exePath);
                if (icon != null) {
                    System.Windows.Media.Imaging.BitmapSource bitmap = System.Windows.Interop.Imaging.CreateBitmapSourceFromHIcon(
                        icon.Handle, Int32Rect.Empty, System.Windows.Media.Imaging.BitmapSizeOptions.FromEmptyOptions());
                    bitmap.Freeze();
                    source = bitmap;
                }
            }
        } catch {
            // Unreadable executable — the row falls back to a letter tile.
        }

        _gameIconCache[exePath] = source;
        return source;
    }

    private void SetGameGpuPreference(string exePath, bool highPerformance) {
        try {
            if (highPerformance) GpuPreference.SetHighPerformance(exePath);
            else GpuPreference.Remove(exePath);
        } catch (Exception ex) {
            GameScanStatusText.Text = $"Couldn't change the GPU preference: {ex.Message}";
            GameScanStatusText.Visibility = Visibility.Visible;
        }
    }

    private async void ScanForGamesButton_Click(object sender, RoutedEventArgs e) {
        var button = (Button)sender;
        button.IsEnabled = false;
        GameScanStatusText.Visibility = Visibility.Visible;
        GameScanStatusText.Text = "Scanning Steam and Epic Games libraries...";

        try {
            List<DetectedGame> detected = await Task.Run(GameDetector.DetectInstalledGames);
            (int added, int removed) = MergeScannedGames(detected);
            _settings.Save();
            RenderGameProfilesList();

            if (detected.Count == 0 && removed == 0) {
                GameScanStatusText.Text = "No installed Steam or Epic games found.";
            } else {
                var parts = new List<string> { $"Found {detected.Count} installed game(s)" };
                if (added > 0) parts.Add($"{added} new");
                if (removed > 0) parts.Add($"{removed} removed (uninstalled or duplicate)");
                if (added == 0 && removed == 0) parts.Add("no changes");
                GameScanStatusText.Text = string.Join(" · ", parts);
            }
        } catch (Exception ex) {
            GameScanStatusText.Text = $"Scan failed: {ex.Message}";
        } finally {
            button.IsEnabled = true;
        }
    }

    // Matches by path first, then by name for scanned games, because Steam's main-executable guess
    // can change after a game update — so a game keeps its chosen mode across rescans.
    private (int Added, int Removed) MergeScannedGames(List<DetectedGame> detected) {
        int removed = CollapseDuplicateGameProfiles();
        var matched = new HashSet<GameProfile>();
        int added = 0;

        foreach (DetectedGame game in detected) {
            string gamePath = PathNormalizer.Normalize(game.ExecutablePath);
            GameProfile? profile = _settings.GameProfiles.FirstOrDefault(p =>
                    string.Equals(PathNormalizer.Normalize(p.ExecutablePath), gamePath, StringComparison.OrdinalIgnoreCase))
                ?? _settings.GameProfiles.FirstOrDefault(p =>
                    p.IsScanned && !matched.Contains(p) && string.Equals(p.Name, game.Name, StringComparison.OrdinalIgnoreCase));

            if (profile == null) {
                profile = new GameProfile { Mode = GameProfile.NoSwitch };
                _settings.GameProfiles.Add(profile);
                added++;
            }

            profile.Name = game.Name;
            profile.ExecutablePath = gamePath;
            profile.IsScanned = true;
            matched.Add(profile);
        }

        removed += _settings.GameProfiles.RemoveAll(p =>
            p.IsScanned && !matched.Contains(p) && !File.Exists(p.ExecutablePath));
        return (added, removed);
    }

    private async void ScanForGamesIfNoneSaved() {
        if (_settings.GameProfiles.Count > 0) return;

        try {
            List<DetectedGame> detected = await Task.Run(GameDetector.DetectInstalledGames);
            if (detected.Count == 0) return;
            MergeScannedGames(detected);
            _settings.Save();
            RenderGameProfilesList();
        } catch {
            // Best-effort — the user can still press Scan.
        }
    }

    // Earlier scans could save one game twice under different path spellings (c:/… and C:\…).
    // Keeps one entry per file, preferring the copy that has a mode chosen.
    private int CollapseDuplicateGameProfiles() {
        int before = _settings.GameProfiles.Count;
        _settings.GameProfiles = _settings.GameProfiles
            .GroupBy(p => PathNormalizer.Normalize(p.ExecutablePath), StringComparer.OrdinalIgnoreCase)
            .Select(group => group.OrderBy(p => p.Mode == GameProfile.NoSwitch).First())
            .ToList();
        return before - _settings.GameProfiles.Count;
    }

    private void BrowseForGameButton_Click(object sender, RoutedEventArgs e) {
        var dialog = new Microsoft.Win32.OpenFileDialog {
            Title = "Select a game executable",
            Filter = "Applications (*.exe)|*.exe"
        };
        if (dialog.ShowDialog(this) != true) return;

        string name = Path.GetFileNameWithoutExtension(dialog.FileName);
        AddGameProfile(name, dialog.FileName, HpFanMode.Performance);
    }

    private void AddGameProfile(string name, string executablePath, HpFanMode mode) {
        if (_settings.GameProfiles.Any(p => string.Equals(p.ExecutablePath, executablePath, StringComparison.OrdinalIgnoreCase)))
            return;

        _settings.GameProfiles.Add(new GameProfile { Name = name, ExecutablePath = executablePath, Mode = mode.ToString() });
        _settings.Save();
        RenderGameProfilesList();
    }

    // ----- About & support ------------------------------------------------------------------

    private string? _pendingReleaseUrl;

    private async void CheckForAppUpdateButton_Click(object sender, RoutedEventArgs e) {
        CheckForAppUpdateButton.IsEnabled = false;
        OpenReleasePageButton.Visibility = Visibility.Collapsed;
        AppUpdateStatusText.Visibility = Visibility.Visible;
        AppUpdateStatusText.Text = "Checking for updates...";

        Version current = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version ?? new Version(1, 0, 0);
        AppUpdateResult result = await AppUpdateChecker.CheckAsync(current);

        if (result.Error != null) {
            AppUpdateStatusText.Text = result.Error;
        } else if (result.UpdateAvailable) {
            AppUpdateStatusText.Text = $"Version {result.LatestVersion} is available (you have {current}).";
            if (!string.IsNullOrEmpty(result.ReleaseUrl)) {
                _pendingReleaseUrl = result.ReleaseUrl;
                OpenReleasePageButton.Visibility = Visibility.Visible;
            }
        } else {
            AppUpdateStatusText.Text = "You're up to date.";
        }

        CheckForAppUpdateButton.IsEnabled = true;
    }

    private void OpenReleasePageButton_Click(object sender, RoutedEventArgs e) {
        if (string.IsNullOrEmpty(_pendingReleaseUrl)) return;
        try {
            Process.Start(new ProcessStartInfo(_pendingReleaseUrl) { UseShellExecute = true });
        } catch {
            // Best-effort — worst case nothing happens and the user can open it manually.
        }
    }

    private void CopyDiagnosticsButton_Click(object sender, RoutedEventArgs e) {
        try {
            System.Windows.Clipboard.SetText(BuildDiagnosticsReport());
            MessageBox.Show(this, "Diagnostics copied to clipboard.", "HP Victus Control",
                MessageBoxButton.OK, MessageBoxImage.Information);
        } catch (Exception ex) {
            MessageBox.Show(this, $"Couldn't copy diagnostics: {ex.Message}", "HP Victus Control",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private string BuildDiagnosticsReport() {
        var sb = new System.Text.StringBuilder();
        Version version = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version ?? new Version(1, 0, 0);

        sb.AppendLine("HP Victus Control diagnostics");
        sb.AppendLine($"App version: {version}");
        sb.AppendLine($"OS: {Environment.OSVersion.VersionString}");

        try {
            using var cpuSearcher = new System.Management.ManagementObjectSearcher("SELECT Name FROM Win32_Processor");
            foreach (System.Management.ManagementBaseObject o in cpuSearcher.Get())
                sb.AppendLine($"CPU: {o["Name"]}");
        } catch { /* best-effort */ }

        try {
            using var gpuSearcher = new System.Management.ManagementObjectSearcher("SELECT Name, DriverVersion FROM Win32_VideoController");
            foreach (System.Management.ManagementBaseObject o in gpuSearcher.Get())
                sb.AppendLine($"GPU: {o["Name"]} (driver {o["DriverVersion"]})");
        } catch { /* best-effort */ }

        try {
            using var biosSearcher = new System.Management.ManagementObjectSearcher("SELECT SMBIOSBIOSVersion FROM Win32_BIOS");
            foreach (System.Management.ManagementBaseObject o in biosSearcher.Get())
                sb.AppendLine($"System BIOS: {o["SMBIOSBIOSVersion"]}");
        } catch { /* best-effort */ }

        sb.AppendLine();
        sb.AppendLine($"HP BIOS control interface available: {_bios.IsAvailable}");
        sb.AppendLine($"Performance mode: {_currentMode}");
        sb.AppendLine($"CPU temp: {TemperatureText.Text}   GPU temp: {GpuTempText.Text}");
        sb.AppendLine($"CPU usage: {CpuUsageText.Text}   GPU usage: {GpuUsageText.Text}");
        sb.AppendLine($"RAM: {RamText.Text}");
        sb.AppendLine($"CPU fan: {CpuFanText.Text}   GPU fan: {GpuFanText.Text}");
        sb.AppendLine(BatteryStatusText.Text);

        return sb.ToString();
    }

    private void ResetToDefaultsButton_Click(object sender, RoutedEventArgs e) {
        MessageBoxResult confirm = MessageBox.Show(this,
            "This resets all app preferences — theme, performance mode, auto-switch, fan curve, alerts, and " +
            "game profiles — back to defaults. Restart HP Victus Control afterward for it to fully take effect. Continue?",
            "Reset to defaults", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.Yes) return;

        new AppSettings().Save();
        MessageBox.Show(this, "Defaults restored. Restart HP Victus Control for the change to fully take effect.",
            "HP Victus Control", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    // ----- Window / tray lifecycle --------------------------------------------------------

    private bool _balloonShown;

    private void MainWindow_Closing(object? sender, System.ComponentModel.CancelEventArgs e) {
        if (_exitRequested) {
            SaveWindowBounds();
            return;
        }

        if (_settings.ExitOnClose) {
            e.Cancel = true; // this Close() is superseded by the real one ExitApplication triggers below
            ExitApplication();
            return;
        }

        e.Cancel = true;
        Hide();

        if (!_balloonShown) {
            _tray.ShowBalloon("HP Victus Control", "Still running in the background. Right-click the tray icon to exit.");
            _balloonShown = true;
        }
    }

    private void ExitApplication() {
        _exitRequested = true;
        _pollTimer.Stop();
        Microsoft.Win32.SystemEvents.PowerModeChanged -= SystemEvents_PowerModeChanged;
        Microsoft.Win32.SystemEvents.UserPreferenceChanged -= SystemEvents_UserPreferenceChanged;
        try {
            UnregisterHotkeys();
        } catch {
            // Best-effort.
        }
        // Reopens OneDrive if a game boost had closed it, and puts back anything else a running game changed.
        _gameBoost.Stop();
        _wifiTuning.Stop();
        RestorePreGameResolution();

        // Max fan the app switched on for a game is the app's to undo, even on the way out.
        if (_activeGameMaxFan && !_preGameMaxFan) {
            try { _bios.SetMaxFanSpeed(false); } catch (HpBiosException) { }
        }

        _tray.Dispose();
        _bios.Dispose();
        Close();
        System.Windows.Application.Current.Shutdown();
    }

    private void ShowBanner(string text) {
        StatusBannerText.Text = text;
        StatusBanner.Visibility = Visibility.Visible;
    }

    private void SetControlsEnabled(bool enabled) {
        BalancedRadio.IsEnabled = enabled;
        PerformanceRadio.IsEnabled = enabled;
        CoolRadio.IsEnabled = enabled;
        AutoPowerCheckBox.IsEnabled = enabled;
        ManualCheckBox.IsEnabled = enabled;
        MaxFanCheckBox.IsEnabled = enabled;
        ApplyFanButton.IsEnabled = enabled;
    }
}
