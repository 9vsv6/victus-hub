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
        else if (_settings.LastTab == "Settings") SettingsTabRadio.IsChecked = true;

        RestoreWindowBounds();

        ApplyTheme(_settings.DarkTheme);
        DarkThemeCheckBox.IsChecked = _settings.DarkTheme;
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

    // ----- Global hotkey: Ctrl+Alt+F12 cycles performance mode from anywhere -----------------

    [DllImport("user32.dll")]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll")]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    private const int CycleModeHotkeyId = 0x4859;
    private const uint ModControl = 0x0002;
    private const uint ModAlt = 0x0001;
    private const uint VkF12 = 0x7B;
    private const int WmHotkey = 0x0312;

    private void RegisterCycleModeHotkey() {
        try {
            IntPtr hwnd = new WindowInteropHelper(this).Handle;
            HwndSource.FromHwnd(hwnd)?.AddHook(HotkeyWndProc);
            RegisterHotKey(hwnd, CycleModeHotkeyId, ModControl | ModAlt, VkF12);
        } catch {
            // Best-effort — another app may already own this key combination.
        }
    }

    private IntPtr HotkeyWndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled) {
        if (msg == WmHotkey && wParam.ToInt32() == CycleModeHotkeyId) {
            CycleModeViaHotkey();
            handled = true;
        }
        return IntPtr.Zero;
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
        SettingsTabPanel.Visibility = SettingsTabRadio.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;

        if (_initializing) return;
        _settings.LastTab = DriversTabRadio.IsChecked == true ? "Drivers"
            : SettingsTabRadio.IsChecked == true ? "Settings" : "Performance";
        _settings.Save();
    }

    private void DarkThemeCheckBox_Changed(object sender, RoutedEventArgs e) {
        bool dark = DarkThemeCheckBox.IsChecked == true;
        ApplyTheme(dark);

        if (_initializing) return;
        _settings.DarkTheme = dark;
        _settings.Save();
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

    private void MainWindow_Loaded(object sender, RoutedEventArgs e) {
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
        if (AutoFanCheckBox.IsChecked != true) return;

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
            string folder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "HP Victus Control", "Downloads");
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
        SortBar.Visibility = Visibility.Collapsed;
        BulkActionsBar.Visibility = Visibility.Collapsed;
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
                UpdatesStatusText.Text = "You're up to date — no newer drivers or BIOS found for this model."
                    + (intelCurrent.Length > 0 ? $" Intel confirms its latest is installed: {intelCurrent}." : "");
            } else {
                _lastUpdates = updates;
                UpdatesStatusText.Text = $"{updates.Count} update(s) available:";
                RenderUpdateRows(updates);
                SortBar.Visibility = Visibility.Visible;
                BulkActionsBar.Visibility = Visibility.Visible;
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

    private void SortMode_Changed(object sender, RoutedEventArgs e) {
        if (_lastUpdates.Count == 0) return;
        RenderUpdateRows(_lastUpdates);
    }

    private static readonly (string Match, string Section)[] SectionRules = {
        ("BIOS", "BIOS"),
        ("Graphics", "Graphics"),
        ("Chipset", "Chipset"),
        ("Network", "Network"),
        ("Firmware", "Storage & firmware"),
    };

    private static string GetSectionName(string category) {
        foreach ((string match, string section) in SectionRules)
            if (category.Contains(match, StringComparison.OrdinalIgnoreCase)) return section;
        return "Other";
    }

    private void RenderUpdateRows(List<HpDriverUpdate> updates) {
        UpdatesListPanel.Children.Clear();
        _updateEntries.Clear();

        List<IGrouping<string, HpDriverUpdate>> groups = updates.GroupBy(u => GetSectionName(u.Category)).ToList();

        IEnumerable<IGrouping<string, HpDriverUpdate>> orderedGroups = SortByTypeRadio.IsChecked == true
            ? groups.OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
            : groups.OrderByDescending(g => g.Max(u => HpDriverUpdateService.ParseReleaseDate(u.ReleaseDate)));

        bool firstSection = true;
        foreach (IGrouping<string, HpDriverUpdate> group in orderedGroups) {
            AddSectionHeader(group.Key, firstSection);
            firstSection = false;

            bool firstRowInSection = true;
            foreach (HpDriverUpdate update in group.OrderByDescending(u => HpDriverUpdateService.ParseReleaseDate(u.ReleaseDate))) {
                if (!firstRowInSection) {
                    UpdatesListPanel.Children.Add(new Border { Height = 1, Background = (Brush)FindResource("BorderBrush2") });
                }
                firstRowInSection = false;
                AddUpdateRow(update);
            }
        }

        UpdateBulkButtonsState();
    }

    private void AddSectionHeader(string name, bool isFirst) {
        UpdatesListPanel.Children.Add(new TextBlock {
            Text = name.ToUpperInvariant(), FontSize = 10, FontWeight = FontWeights.Bold,
            Foreground = (Brush)FindResource("AccentBrush"),
            Margin = new Thickness(0, isFirst ? 2 : 20, 0, 6)
        });
    }

    private void AddUpdateRow(HpDriverUpdate update) {
        var row = new Grid { Margin = new Thickness(0, 10, 0, 10) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var selectCheckBox = new CheckBox {
            Style = (Style)FindResource("ModernCheckBox"), VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 10, 0)
        };
        Grid.SetColumn(selectCheckBox, 0);
        row.Children.Add(selectCheckBox);

        var info = new StackPanel();
        info.Children.Add(new TextBlock {
            Text = update.Title, FontWeight = FontWeights.SemiBold, FontSize = 12, TextWrapping = TextWrapping.Wrap,
            Foreground = (Brush)FindResource("TextPrimaryBrush")
        });
        info.Children.Add(new TextBlock {
            Text = $"{update.Category} · v{update.Version} · {update.FileSize}" +
                   (string.IsNullOrEmpty(update.ReleaseDate) ? "" : $" · {update.ReleaseDate}"),
            FontSize = 11, Margin = new Thickness(0, 2, 0, 0), TextWrapping = TextWrapping.Wrap,
            Foreground = (Brush)FindResource("TextSecondaryBrush")
        });
        Grid.SetColumn(info, 1);
        row.Children.Add(info);

        var actionPanel = new StackPanel {
            Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(10, 0, 0, 0)
        };
        Grid.SetColumn(actionPanel, 2);
        row.Children.Add(actionPanel);

        var downloadButton = new Button {
            Content = "Download", Style = (Style)FindResource("PrimaryButton"),
            Padding = new Thickness(14, 6, 14, 6)
        };
        actionPanel.Children.Add(downloadButton);

        var entry = new UpdateEntry {
            Update = update, SelectCheckBox = selectCheckBox, DownloadButton = downloadButton, ActionPanel = actionPanel
        };
        _updateEntries.Add(entry);

        selectCheckBox.Checked += (_, _) => UpdateBulkButtonsState();
        selectCheckBox.Unchecked += (_, _) => UpdateBulkButtonsState();
        downloadButton.Click += async (_, _) => await DownloadEntryAsync(entry);

        UpdatesListPanel.Children.Add(row);
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
            string folder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "HP Victus Control", "Downloads");
            entry.DownloadedPath = await HpDriverUpdateService.DownloadUpdateAsync(entry.Update, folder);
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

        bool anyBios = selected.Any(x => x.Update.Category.Contains("BIOS", StringComparison.OrdinalIgnoreCase));
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

            int? exitCode = await RunInstallerProcessAsync(entry.DownloadedPath);
            if (exitCode == 0) {
                succeeded++;
                InstalledUpdateHistory.MarkInstalled(entry.Update);
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

    private async Task RunInstallerAsync(HpDriverUpdate update, string filePath, Button runButton) {
        bool isBios = update.Category.Contains("BIOS", StringComparison.OrdinalIgnoreCase);
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

    // ----- Per-game profiles ----------------------------------------------------------------

    // At most one tracked game "owns" the current mode at a time; when it exits, the mode from
    // just before it launched is restored.
    private string? _activeGameProfileExeName;
    private HpFanMode _preGameMode;
    private bool _activeGameMaxFan;
    private bool _preGameMaxFan;

    private void CheckGameProfiles() {
        var tracked = new List<(string ExeName, string Name, HpFanMode? Mode, int RefreshRateHz, bool MaxFan)>();
        foreach (GameProfile profile in _settings.GameProfiles) {
            HpFanMode? mode = Enum.TryParse(profile.Mode, out HpFanMode parsed) ? parsed : null;
            int hz = _refreshRates.Contains(profile.RefreshRateHz) ? profile.RefreshRateHz : 0;
            if (mode == null && hz == 0 && !profile.MaxFan) continue;

            string exeName = Path.GetFileNameWithoutExtension(profile.ExecutablePath);
            if (exeName.Length > 0) tracked.Add((exeName, profile.Name, mode, hz, profile.MaxFan));
        }
        if (tracked.Count == 0 && _activeGameProfileExeName == null) return;

        HashSet<string> running = GetRunningProcessNames();

        if (_activeGameProfileExeName != null) {
            if (running.Contains(_activeGameProfileExeName)) {
                KeepMaxFanAsserted();
                return;
            }

            string endedGame = _activeGameProfileExeName;
            // Re-applying the earlier mode also puts back that mode's refresh rate.
            ApplyMode(_preGameMode);
            if (_activeGameMaxFan) {
                _activeGameMaxFan = false;
                MaxFanCheckBox.IsChecked = _preGameMaxFan;
            }
            _activeGameProfileExeName = null;
            _tray.ShowBalloon("HP Victus Control", $"{endedGame} closed — restored {_preGameMode} mode");
        }

        foreach ((string exeName, string name, HpFanMode? mode, int hz, bool maxFan) in tracked) {
            if (!running.Contains(exeName)) continue;

            _preGameMode = _currentMode;
            if (mode.HasValue) ApplyMode(mode.Value);
            if (hz > 0) ApplyRefreshRate(hz);
            if (maxFan) {
                _preGameMaxFan = MaxFanCheckBox.IsChecked == true;
                _activeGameMaxFan = true;
                // The checkbox's own handler is what talks to the BIOS and the tray menu.
                MaxFanCheckBox.IsChecked = true;
                KeepMaxFanAsserted();
            }
            _activeGameProfileExeName = exeName;

            var changes = new List<string>();
            if (mode.HasValue) changes.Add($"{mode.Value} mode");
            if (hz > 0) changes.Add($"{hz}Hz");
            if (maxFan) changes.Add("max fan");
            _tray.ShowBalloon("HP Victus Control", $"{name} detected — switched to {string.Join(", ", changes)}");
            break;
        }
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

        bool first = true;
        foreach (GameProfile profile in _settings.GameProfiles.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase)) {
            if (!first) {
                var divider = new Border { Height = 1, Margin = new Thickness(0, 12, 0, 12) };
                divider.SetResourceReference(Border.BackgroundProperty, "BorderBrush2");
                GameProfilesListPanel.Children.Add(divider);
            }
            first = false;
            GameProfilesListPanel.Children.Add(BuildGameRow(profile));
        }
    }

    private FrameworkElement BuildGameRow(GameProfile profile) {
        bool installed = File.Exists(profile.ExecutablePath);

        var row = new Grid();
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        FrameworkElement icon = BuildAppIcon(profile.ExecutablePath, profile.Name);
        Grid.SetColumn(icon, 0);
        row.Children.Add(icon);

        var body = new StackPanel { Margin = new Thickness(12, 0, 0, 0) };

        var nameText = new TextBlock { Text = profile.Name, FontWeight = FontWeights.SemiBold, FontSize = 13, TextWrapping = TextWrapping.Wrap };
        nameText.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimaryBrush");
        body.Children.Add(nameText);

        var pathText = new TextBlock {
            Text = installed ? profile.ExecutablePath : $"Not found: {profile.ExecutablePath}",
            FontSize = 10, Margin = new Thickness(0, 2, 0, 8), TextWrapping = TextWrapping.Wrap
        };
        pathText.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
        body.Children.Add(pathText);

        body.Children.Add(BuildGameModeSelector(profile));
        if (_refreshRates.Count > 1) body.Children.Add(BuildGameRefreshRateSelector(profile));
        if (NvidiaFrameLimiter.IsAvailable && installed) body.Children.Add(BuildGameFrameLimitSelector(profile));

        if (NvidiaGpuSensor.IsAvailable && installed) {
            var gpuToggle = new CheckBox {
                Content = "Use NVIDIA GPU", Style = (Style)FindResource("GlowToggle"), Margin = new Thickness(0, 10, 0, 0),
                IsChecked = GpuPreference.IsHighPerformance(profile.ExecutablePath)
            };
            gpuToggle.Checked += (_, _) => SetGameGpuPreference(profile.ExecutablePath, true);
            gpuToggle.Unchecked += (_, _) => SetGameGpuPreference(profile.ExecutablePath, false);
            body.Children.Add(gpuToggle);
        }

        var maxFanToggle = new CheckBox {
            Content = "Max fan while playing", Style = (Style)FindResource("GlowToggle"), Margin = new Thickness(0, 8, 0, 0),
            ToolTip = "Runs both fans flat out while this game is open, then puts the fans back afterwards",
            IsChecked = profile.MaxFan
        };
        maxFanToggle.Checked += (_, _) => SaveGameMaxFan(profile, true);
        maxFanToggle.Unchecked += (_, _) => SaveGameMaxFan(profile, false);
        body.Children.Add(maxFanToggle);

        Grid.SetColumn(body, 1);
        row.Children.Add(body);

        // Scanned games reappear on the next scan, so only manually added ones get a Remove button.
        if (!profile.IsScanned) {
            var removeButton = new Button {
                Content = "Remove", Style = (Style)FindResource("SecondaryButton"),
                Padding = new Thickness(12, 6, 12, 6), Margin = new Thickness(10, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Top
            };
            removeButton.Click += (_, _) => {
                _settings.GameProfiles.Remove(profile);
                _settings.Save();
                RenderGameProfilesList();
            };
            Grid.SetColumn(removeButton, 2);
            row.Children.Add(removeButton);
        }

        return row;
    }

    private FrameworkElement BuildGameModeSelector(GameProfile profile) {
        (string Label, string Value)[] modes = {
            ("Off", GameProfile.NoSwitch),
            ("Balanced", nameof(HpFanMode.Balanced)),
            ("Performance", nameof(HpFanMode.Performance)),
            ("Cool", nameof(HpFanMode.Cool)),
        };

        return BuildSegmentTrack(modes.Select(m => (
            m.Label,
            m.Value == GameProfile.NoSwitch ? "Don't switch modes for this game" : (string?)null,
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

        FrameworkElement track = BuildSegmentTrack(options);
        track.Margin = new Thickness(0, 8, 0, 0);
        return track;
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

    // The driver's frame limiter takes any rate from 20 to 1000 fps, so this is a plain number
    // box rather than a list of presets: type a cap, or leave it empty for no cap at all.
    private FrameworkElement BuildGameFrameLimitSelector(GameProfile profile) {
        _frameLimits.TryGetValue(profile.ExecutablePath, out int current);

        var input = new TextBox {
            Width = 46, Text = current > 0 ? current.ToString() : "", MaxLength = 4,
            BorderThickness = new Thickness(0), Background = System.Windows.Media.Brushes.Transparent,
            FontSize = 12, TextAlignment = TextAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center,
            ToolTip = $"{NvidiaFrameLimiter.MinFps}–{NvidiaFrameLimiter.MaxFps} fps, or empty for no cap"
        };
        input.SetResourceReference(System.Windows.Controls.Control.ForegroundProperty, "TextPrimaryBrush");
        input.SetResourceReference(System.Windows.Controls.Primitives.TextBoxBase.CaretBrushProperty, "TextPrimaryBrush");

        var inputBox = new Border {
            CornerRadius = new CornerRadius(9), Padding = new Thickness(8, 5, 8, 5),
            VerticalAlignment = VerticalAlignment.Center, Child = input
        };
        inputBox.SetResourceReference(Border.BackgroundProperty, "TrackBrush");

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

        var label = new TextBlock {
            Text = "FPS cap", FontSize = 11, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0)
        };
        label.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");

        var hint = new TextBlock {
            Text = "empty = no cap", FontSize = 10, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0)
        };
        hint.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");

        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 0) };
        row.Children.Add(label);
        row.Children.Add(inputBox);
        row.Children.Add(apply);
        row.Children.Add(hint);
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
        try {
            UnregisterHotKey(new WindowInteropHelper(this).Handle, CycleModeHotkeyId);
        } catch {
            // Best-effort.
        }
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
