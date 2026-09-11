using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
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

        ApplyTheme(_settings.DarkTheme);
        DarkThemeCheckBox.IsChecked = _settings.DarkTheme;
        TempAlertCheckBox.IsChecked = _settings.TempAlertsEnabled;
        TempAlertSlider.Value = _settings.TempAlertThreshold;

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
            ("CardBrush", Color.FromRgb(0xFF, 0xFF, 0xFF), Color.FromRgb(0x1E, 0x21, 0x26)),
            ("BorderBrush2", Color.FromRgb(0xE4, 0xE6, 0xEB), Color.FromRgb(0x2C, 0x30, 0x38)),
            ("TrackBrush", Color.FromRgb(0xEE, 0xF0, 0xF3), Color.FromRgb(0x26, 0x29, 0x2F)),
            ("TextPrimaryBrush", Color.FromRgb(0x1B, 0x1F, 0x24), Color.FromRgb(0xF2, 0xF3, 0xF5)),
            ("TextSecondaryBrush", Color.FromRgb(0x8A, 0x8F, 0x98), Color.FromRgb(0x9A, 0xA0, 0xAA)),
            ("WindowBackgroundBrush", Color.FromRgb(0xF4, 0xF5, 0xF7), Color.FromRgb(0x12, 0x13, 0x16)),
        };

        foreach ((string key, Color light, Color darkColor) in palette)
            Resources[key] = new SolidColorBrush(dark ? darkColor : light);
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

        RenderGameProfilesList();
        PopulateRefreshRateOptions();

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
        if (MemoryInfo.TryGetUsage(out double usedGb, out double totalGb))
            RamText.Text = $"{usedGb:0.#}/{totalGb:0.#} GB";

        if (CpuUsageSensor.TryGetUsagePercent(out double cpuUsage))
            CpuUsageText.Text = $"{cpuUsage:0}%";

        RefreshGpuStats();

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
                tempText = $"{acpiTemp:0.#}°C";
                cpuTempValue = acpiTemp;
            }
        } catch (HpBiosException) {
            if (AcpiThermalSensor.TryRead(out double acpiTemp)) {
                tempText = $"{acpiTemp:0.#}°C";
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

    // nvidia-smi is a subprocess call, so this runs off the poll tick instead of blocking it.
    private bool _gpuStatsQueryInFlight;

    private async void RefreshGpuStats() {
        if (!NvidiaGpuSensor.IsAvailable || _gpuStatsQueryInFlight) return;

        _gpuStatsQueryInFlight = true;
        (double? temperature, double? utilization) = await NvidiaGpuSensor.TryReadStatsAsync();
        _gpuStatsQueryInFlight = false;

        GpuTempText.Text = temperature.HasValue ? $"{temperature.Value:0.#}°C" : "N/A";
        GpuUsageText.Text = utilization.HasValue ? $"{utilization.Value:0}%" : "N/A";
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

    // Performance mode bumps the panel to its highest refresh rate; Balanced/Cool drop it back
    // to 60Hz to save power, mirroring how OEM gaming-hub apps pair refresh rate with profile.
    private void ApplyRefreshRateForMode(HpFanMode mode) {
        try {
            if (mode == HpFanMode.Performance) {
                int rate = _settings.PerformanceRefreshRateHz > 0
                    ? _settings.PerformanceRefreshRateHz
                    : DisplayRefreshRate.GetMaxRefreshRate() ?? 60;
                DisplayRefreshRate.SetRefreshRate(rate);
            } else {
                DisplayRefreshRate.SetRefreshRate(60);
            }
        } catch {
            // Best-effort — not every display/driver combination supports this cleanly.
        }
    }

    private void PopulateRefreshRateOptions() {
        List<int> rates = DisplayRefreshRate.GetSupportedRefreshRates();
        RefreshRateOptionsPanel.Children.Clear();

        if (rates.Count <= 1) {
            RefreshRateLabel.Visibility = Visibility.Collapsed;
            RefreshRateTrack.Visibility = Visibility.Collapsed;
            return;
        }

        int target = _settings.PerformanceRefreshRateHz > 0 && rates.Contains(_settings.PerformanceRefreshRateHz)
            ? _settings.PerformanceRefreshRateHz
            : rates[^1];

        foreach (int rate in rates) {
            var radio = new RadioButton {
                Content = $"{rate}Hz", GroupName = "RefreshRateOption", Style = (Style)FindResource("SegmentRadio"),
                Tag = rate
            };
            radio.Checked += RefreshRateOption_Checked;
            RefreshRateOptionsPanel.Children.Add(radio);
            if (rate == target) radio.IsChecked = true;
        }
    }

    private void RefreshRateOption_Checked(object sender, RoutedEventArgs e) {
        if (_initializing) return;

        int rate = (int)((RadioButton)sender).Tag;
        _settings.PerformanceRefreshRateHz = rate;
        _settings.Save();

        if (_currentMode == HpFanMode.Performance) DisplayRefreshRate.SetRefreshRate(rate);
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

            if (updates.Count == 0) {
                UpdatesStatusText.Text = "You're up to date — no newer drivers or BIOS found for this model.";
            } else {
                _lastUpdates = updates;
                UpdatesStatusText.Text = $"{updates.Count} update(s) available:";
                RenderUpdateRows(SortUpdates(updates));
                SortBar.Visibility = Visibility.Visible;
                BulkActionsBar.Visibility = Visibility.Visible;
            }
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
        RenderUpdateRows(SortUpdates(_lastUpdates));
    }

    private List<HpDriverUpdate> SortUpdates(List<HpDriverUpdate> updates) {
        if (SortByTypeRadio.IsChecked == true)
            return updates
                .OrderBy(u => u.Category, StringComparer.OrdinalIgnoreCase)
                .ThenByDescending(u => HpDriverUpdateService.ParseReleaseDate(u.ReleaseDate))
                .ToList();

        return updates.OrderByDescending(u => HpDriverUpdateService.ParseReleaseDate(u.ReleaseDate)).ToList();
    }

    private void RenderUpdateRows(List<HpDriverUpdate> updates) {
        UpdatesListPanel.Children.Clear();
        _updateEntries.Clear();
        foreach (HpDriverUpdate update in updates) AddUpdateRow(update);
        UpdateBulkButtonsState();
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
        if (UpdatesListPanel.Children.Count > 1) {
            UpdatesListPanel.Children.Insert(UpdatesListPanel.Children.Count - 1, new Border {
                Height = 1, Background = (Brush)FindResource("BorderBrush2")
            });
        }
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

    private void CheckGameProfiles() {
        if (_settings.GameProfiles.Count == 0) return;

        if (_activeGameProfileExeName != null) {
            bool stillRunning = Process.GetProcessesByName(_activeGameProfileExeName).Length > 0;
            if (!stillRunning) {
                ApplyMode(_preGameMode);
                _activeGameProfileExeName = null;
            } else {
                return;
            }
        }

        foreach (GameProfile profile in _settings.GameProfiles) {
            string exeName = Path.GetFileNameWithoutExtension(profile.ExecutablePath);
            if (exeName.Length == 0 || Process.GetProcessesByName(exeName).Length == 0) continue;
            if (!Enum.TryParse(profile.Mode, out HpFanMode gameMode)) continue;

            _preGameMode = _currentMode;
            ApplyMode(gameMode);
            _activeGameProfileExeName = exeName;
            break;
        }
    }

    private void RenderGameProfilesList() {
        GameProfilesListPanel.Children.Clear();
        NoGameProfilesText.Visibility = _settings.GameProfiles.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        foreach (GameProfile profile in _settings.GameProfiles) {
            var row = new Grid { Margin = new Thickness(0, 6, 0, 6) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var info = new StackPanel();
            info.Children.Add(new TextBlock {
                Text = profile.Name, FontWeight = FontWeights.SemiBold, FontSize = 12, TextWrapping = TextWrapping.Wrap,
                Foreground = (Brush)FindResource("TextPrimaryBrush")
            });
            info.Children.Add(new TextBlock {
                Text = $"{profile.Mode} mode", FontSize = 11, Margin = new Thickness(0, 2, 0, 0),
                Foreground = (Brush)FindResource("TextSecondaryBrush")
            });
            Grid.SetColumn(info, 0);
            row.Children.Add(info);

            var removeButton = new Button {
                Content = "Remove", Style = (Style)FindResource("SecondaryButton"),
                Padding = new Thickness(12, 6, 12, 6)
            };
            removeButton.Click += (_, _) => {
                _settings.GameProfiles.Remove(profile);
                _settings.Save();
                RenderGameProfilesList();
            };
            Grid.SetColumn(removeButton, 1);
            row.Children.Add(removeButton);

            GameProfilesListPanel.Children.Add(row);
        }
    }

    private async void ScanForGamesButton_Click(object sender, RoutedEventArgs e) {
        var button = (Button)sender;
        button.IsEnabled = false;
        DetectedGamesListPanel.Children.Clear();
        GameScanStatusText.Visibility = Visibility.Visible;
        GameScanStatusText.Text = "Scanning Steam and Epic Games libraries...";

        try {
            List<DetectedGame> detected = await Task.Run(GameDetector.DetectInstalledGames);
            List<DetectedGame> newOnes = detected
                .Where(d => !_settings.GameProfiles.Any(p => string.Equals(p.ExecutablePath, d.ExecutablePath, StringComparison.OrdinalIgnoreCase)))
                .ToList();

            if (detected.Count == 0) {
                GameScanStatusText.Text = "No installed Steam or Epic games found.";
            } else if (newOnes.Count == 0) {
                GameScanStatusText.Text = $"Found {detected.Count} game(s) — all already tracked.";
            } else {
                GameScanStatusText.Text = $"Found {newOnes.Count} game(s) not yet tracked:";
                foreach (DetectedGame game in newOnes) AddDetectedGameRow(game);
            }
        } catch (Exception ex) {
            GameScanStatusText.Text = $"Scan failed: {ex.Message}";
        } finally {
            button.IsEnabled = true;
        }
    }

    private void AddDetectedGameRow(DetectedGame game) {
        var row = new Grid { Margin = new Thickness(0, 6, 0, 6) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var info = new StackPanel();
        info.Children.Add(new TextBlock {
            Text = game.Name, FontWeight = FontWeights.SemiBold, FontSize = 12, TextWrapping = TextWrapping.Wrap,
            Foreground = (Brush)FindResource("TextPrimaryBrush")
        });
        info.Children.Add(new TextBlock {
            Text = game.ExecutablePath, FontSize = 10, Margin = new Thickness(0, 2, 0, 0), TextWrapping = TextWrapping.Wrap,
            Foreground = (Brush)FindResource("TextSecondaryBrush")
        });
        Grid.SetColumn(info, 0);
        row.Children.Add(info);

        var addButton = new Button {
            Content = "Add as Performance", Style = (Style)FindResource("PrimaryButton"),
            Padding = new Thickness(12, 6, 12, 6)
        };
        addButton.Click += (_, _) => {
            AddGameProfile(game.Name, game.ExecutablePath, HpFanMode.Performance);
            DetectedGamesListPanel.Children.Remove(row);
        };
        Grid.SetColumn(addButton, 1);
        row.Children.Add(addButton);

        DetectedGamesListPanel.Children.Add(row);
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

    // ----- Window / tray lifecycle --------------------------------------------------------

    private bool _balloonShown;

    private void MainWindow_Closing(object? sender, System.ComponentModel.CancelEventArgs e) {
        if (_exitRequested) return;

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
