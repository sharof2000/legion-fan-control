namespace LegionFanTray;

/// <summary>
/// The double-click window. Closing hides it; only Exit from the tray ends the
/// process, because exiting is what releases the fans.
/// </summary>
internal sealed class DashboardForm : Form
{
    // Light palette. Every colour used in this window comes from here or from
    // the accent constants below -- no ad-hoc Color.FromArgb in the layout code.
    private static readonly Color Bg = Color.FromArgb(250, 250, 250);
    private static readonly Color Fg = Color.FromArgb(31, 31, 35);
    private static readonly Color Dim = Color.FromArgb(106, 106, 114);

    private static readonly Color ButtonFace = Color.FromArgb(242, 242, 244);
    private static readonly Color ButtonBorder = Color.FromArgb(198, 198, 204);
    private static readonly Color AccentRed = Color.FromArgb(196, 52, 43);
    private static readonly Color AccentGreen = Color.FromArgb(46, 139, 87);
    private static readonly Color OkText = Color.FromArgb(40, 130, 70);
    private static readonly Color ErrText = Color.FromArgb(190, 50, 45);
    private static readonly Color WarnText = Color.FromArgb(200, 120, 20);

    // Steps of 10. One control only: the release point is derived, never set
    // directly, so it is impossible to pick a pair with no hysteresis.
    private static readonly int[] EngageValues = { 50, 60, 70, 80, 90, 100 };

    /// <summary>
    /// How far below the engage point the fans release. A single threshold for
    /// both directions would oscillate: max at 80, cool to 79, release, climb
    /// to 80, max again -- a cycle every few seconds, audibly worse than either
    /// state. 10 degrees plus the longer exit sample count is the deadband.
    /// </summary>
    private const int ReleaseOffsetC = 10;

    private readonly FanController _controller;
    private readonly Settings _settings;

    private readonly Panel _gaugePanel = new();
    private readonly Panel _numbersPanel = new();
    private readonly GaugeControl _cpuGauge = new();
    private readonly GaugeControl _gpuGauge = new();
    private readonly Label _cpuBig = new();
    private readonly Label _gpuBig = new();
    private readonly RpmBar _fan0 = new();
    private readonly RpmBar _fan1 = new();
    private readonly Label _status = new();
    private readonly Label _result = new();

    private readonly Button _maxButton = new();
    private readonly Button _releaseButton = new();
    private readonly Button _quietButton = new();
    private readonly Button _balancedButton = new();
    private readonly Button _perfButton = new();
    private readonly Button _autoMaxButton = new();
    private readonly Button[] _profileButtons;
    private readonly Button _restoreButton = new();
    private readonly Label _profileHeading = new();
    private readonly Label _snapshotLabel = new();
    private readonly Button _viewButton = new();

    private readonly ComboBox _highCombo = new();
    private readonly Label _releaseLabel = new();
    private readonly ComboBox _capCombo = new();
    private readonly Label _capStatus = new();

    private readonly TabControl _tabs = new();
    private readonly TabPage _fansPage = new("Fans");
    private readonly TabPage _gpuPage = new("GPU / Power");
    private readonly TabPage _amdPage = new("AMD");

    private readonly Label _nvStatus = new();
    private readonly ToggleSwitch[] _powerSwitches;
    private readonly ToggleSwitch _gpuSwitch = new();
    private readonly ListBox _appList = new();
    private readonly ComboBox _prefCombo = new();
    private readonly Button _addAppButton = new();
    private readonly Button _removeAppButton = new();
    private readonly Label _gpuResult = new();

    private readonly Label _amdStatus = new();
    private readonly ToggleSwitch[] _amdSwitches;
    private readonly ComboBox _fpsCombo = new();
    private readonly Label _amdResult = new();
    private bool _suppressAmdEvents;

    /// <summary>
    /// Whichever page the Build* helpers are currently populating. The existing
    /// layout is absolutely positioned and every helper ended in
    /// Controls.Add(this), so redirecting that one call is all it takes to move
    /// the whole dashboard onto a tab page with every coordinate still valid.
    /// </summary>
    private Control _host;

    /// <summary>
    /// The powercfg switch rows, paired with the settings flag each one
    /// persists. A table rather than three near-identical handlers, so adding
    /// a row later is one line here and one PowerToggle in PowerPlan.
    /// </summary>
    private static readonly (PowerToggle Toggle, Func<Settings, bool> Get, Action<Settings, bool> Set)[] PowerRows =
    {
        (PowerPlan.LockCpuBothRails, s => s.LockCpuBothRails, (s, v) => s.LockCpuBothRails = v),
        (PowerPlan.NeverAutoBatterySaver, s => s.DisableBatterySaverAuto, (s, v) => s.DisableBatterySaverAuto = v),
        (PowerPlan.PcieFullPowerOnBattery, s => s.KeepPcieFullPower, (s, v) => s.KeepPcieFullPower = v),
    };

    private bool _busy;
    private bool _suppressThresholdEvents;
    private bool _suppressCapEvents;
    private bool _suppressAppEvents;

    public DashboardForm(FanController controller, Settings settings)
    {
        _controller = controller;
        _settings = settings;
        _powerSwitches = PowerRows.Select(_ => new ToggleSwitch()).ToArray();
        _amdSwitches = AmdGpu.All.Select(_ => new ToggleSwitch()).ToArray();
        _profileButtons = PowerProfile.All.Select(_ => new Button()).ToArray();
        _host = this;

        Text = AppVersion.Title;
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        // 320x488 of content plus the tab strip and the page insets, then the
        // profile row below that. Growing the window rather than moving controls
        // keeps every coordinate below valid.
        ClientSize = new Size(336, 616);
        BackColor = Bg;
        ForeColor = Fg;
        Font = new Font("Segoe UI", 9f);

        // Derived, not stored-and-trusted: a hand-edited settings.json could
        // otherwise show a release point the single dropdown cannot express.
        _settings.LowThresholdC = _settings.HighThresholdC - ReleaseOffsetC;
        _settings.Clamp();

        BuildTabs();

        _host = _fansPage;
        BuildGauges();
        BuildNumbers();
        BuildBars();
        BuildStatus();
        BuildButtons();
        BuildThresholds();
        BuildCpuCap();
        BuildProfiles();

        _host = _gpuPage;
        BuildGpuTab();

        _host = _amdPage;
        BuildAmdTab();

        ApplyViewMode();
        SyncGpuTabFromSettings();
    }

    // --- construction -------------------------------------------------------

    /// <summary>
    /// GaugeControl draws a circle of (Width - 20) centred in its bounds, so a
    /// control wider than the circle silently inflates the outer margins and
    /// the spacing stops looking even. These are sized to the circle: 138 wide
    /// gives a 118 px circle with exactly 10 px of internal padding each side,
    /// which puts 28 px of clear space at the left edge, between the two, and
    /// at the right edge. Height must stay >= width + 16 or the height, not the
    /// width, becomes the limiting dimension and the circle shrinks again.
    /// </summary>
    private void BuildGauges()
    {
        _gaugePanel.SetBounds(0, 8, ClientSize.Width, 156);
        _gaugePanel.BackColor = Bg;

        _cpuGauge.SetBounds(18, 0, 138, 156);
        _cpuGauge.Caption = "CPU";
        _cpuGauge.BackColor = Bg;
        _cpuGauge.ForeColor = Fg;

        _gpuGauge.SetBounds(164, 0, 138, 156);
        _gpuGauge.Caption = "GPU";
        _gpuGauge.BackColor = Bg;
        _gpuGauge.ForeColor = Fg;

        _gaugePanel.Controls.Add(_cpuGauge);
        _gaugePanel.Controls.Add(_gpuGauge);
        _host.Controls.Add(_gaugePanel);
    }

    private void BuildNumbers()
    {
        _numbersPanel.SetBounds(0, 8, ClientSize.Width, 156);
        _numbersPanel.BackColor = Bg;

        // 38pt so a three-digit reading cannot clip at 140 wide.
        var bigFont = new Font("Segoe UI", 38f, FontStyle.Bold);
        var capFont = new Font("Segoe UI", 9f);

        var cpuCap = new Label { Text = "CPU", Font = capFont, ForeColor = Dim, AutoSize = false, TextAlign = ContentAlignment.MiddleCenter };
        cpuCap.SetBounds(16, 8, 140, 18);
        var gpuCap = new Label { Text = "GPU", Font = capFont, ForeColor = Dim, AutoSize = false, TextAlign = ContentAlignment.MiddleCenter };
        gpuCap.SetBounds(164, 8, 140, 18);

        _cpuBig.Font = bigFont;
        _cpuBig.ForeColor = Fg;
        _cpuBig.AutoSize = false;
        _cpuBig.TextAlign = ContentAlignment.MiddleCenter;
        _cpuBig.SetBounds(16, 30, 140, 100);

        _gpuBig.Font = bigFont;
        _gpuBig.ForeColor = Fg;
        _gpuBig.AutoSize = false;
        _gpuBig.TextAlign = ContentAlignment.MiddleCenter;
        _gpuBig.SetBounds(164, 30, 140, 100);

        _numbersPanel.Controls.Add(cpuCap);
        _numbersPanel.Controls.Add(gpuCap);
        _numbersPanel.Controls.Add(_cpuBig);
        _numbersPanel.Controls.Add(_gpuBig);
        _host.Controls.Add(_numbersPanel);
    }

    private void BuildBars()
    {
        _fan0.SetBounds(16, 168, 288, 32);
        _fan0.Caption = "Fan 0";
        _fan0.BackColor = Bg;
        _fan0.ForeColor = Fg;

        _fan1.SetBounds(16, 202, 288, 32);
        _fan1.Caption = "Fan 1";
        _fan1.BackColor = Bg;
        _fan1.ForeColor = Fg;

        _host.Controls.Add(_fan0);
        _host.Controls.Add(_fan1);
    }

    private void BuildStatus()
    {
        _status.SetBounds(16, 240, 288, 18);
        _status.ForeColor = Fg;
        _status.AutoSize = false;
        _host.Controls.Add(_status);

        _result.SetBounds(16, 260, 288, 30);
        _result.ForeColor = Dim;
        _result.AutoSize = false;
        _host.Controls.Add(_result);
    }

    private Button StyleButton(Button b, string text, int x, int y, int w, int h)
    {
        b.Text = text;
        b.SetBounds(x, y, w, h);
        b.FlatStyle = FlatStyle.Flat;
        b.FlatAppearance.BorderColor = ButtonBorder;
        b.BackColor = ButtonFace;
        b.ForeColor = Fg;
        b.UseVisualStyleBackColor = false;
        _host.Controls.Add(b);
        return b;
    }

    private void BuildButtons()
    {
        StyleButton(_maxButton, "FANS MAX", 16, 298, 140, 32);
        _maxButton.BackColor = AccentRed;
        _maxButton.ForeColor = Color.White;   // forced: dark red face on a light form
        _maxButton.FlatAppearance.BorderColor = AccentRed;
        _maxButton.Click += async (_, _) => await RunWriteAsync(() => _controller.EngageMaxAsync(FanOwner.Manual));

        StyleButton(_releaseButton, "RELEASE", 164, 298, 140, 32);
        _releaseButton.Click += async (_, _) => await RunWriteAsync(() => _controller.ReleaseAsync(FanOwner.Manual));

        StyleButton(_quietButton, "Quiet", 16, 336, 92, 28);
        _quietButton.Click += async (_, _) => await RunWriteAsync(() => _controller.SetModeAsync(LenovoWmi.ModeQuiet));

        StyleButton(_balancedButton, "Balanced", 114, 336, 92, 28);
        _balancedButton.Click += async (_, _) => await RunWriteAsync(() => _controller.SetModeAsync(LenovoWmi.ModeBalanced));

        StyleButton(_perfButton, "Performance", 212, 336, 92, 28);
        _perfButton.Click += async (_, _) => await RunWriteAsync(() => _controller.SetModeAsync(LenovoWmi.ModePerformance));

        StyleButton(_autoMaxButton, "Auto-max: off", 16, 372, 140, 28);
        _autoMaxButton.Click += (_, _) =>
        {
            _settings.AutoMaxEnabled = !_settings.AutoMaxEnabled;
            _settings.Save();
            UpdateAutoMaxButton();
            AutoMaxChanged?.Invoke();
        };

        StyleButton(_viewButton, "View", 164, 372, 140, 28);
        _viewButton.Click += (_, _) =>
        {
            _settings.ViewMode = _settings.ViewMode == ViewMode.Gauge ? ViewMode.Numbers : ViewMode.Gauge;
            _settings.Save();
            ApplyViewMode();
        };
    }

    private void BuildThresholds()
    {
        AddLabel("Engage at", 16, 408, 60);
        ConfigureCombo(_highCombo, EngageValues, _settings.HighThresholdC, 80, 408);

        _releaseLabel.SetBounds(142, 408, 162, 24);
        _releaseLabel.ForeColor = Dim;
        _releaseLabel.AutoSize = false;
        _releaseLabel.TextAlign = ContentAlignment.MiddleLeft;
        _host.Controls.Add(_releaseLabel);
        ShowReleasePoint();

        AddLabel("AC-only. Never releases fans you pinned by hand.", 16, 466, 288);
    }

    /// <summary>
    /// CPU max processor state (powercfg). Not fan control -- it cuts heat at
    /// the source so the fans have less to remove. 99% disables Turbo.
    /// </summary>
    private void BuildCpuCap()
    {
        AddLabel("CPU cap", 16, 438, 54);

        _capCombo.DropDownStyle = ComboBoxStyle.DropDownList;
        _capCombo.FlatStyle = FlatStyle.Standard;
        _capCombo.BackColor = Color.White;
        _capCombo.ForeColor = Fg;
        _capCombo.SetBounds(74, 438, 58, 24);
        foreach (int v in CpuCap.Values) _capCombo.Items.Add(v);
        _capCombo.SelectedIndex = 0;
        _capCombo.SelectedIndexChanged += async (_, _) => await OnCapChangedAsync();
        _host.Controls.Add(_capCombo);

        AddLabel("%", 136, 438, 14);

        _capStatus.SetBounds(156, 438, 148, 24);
        _capStatus.ForeColor = Dim;
        _capStatus.AutoSize = false;
        _capStatus.TextAlign = ContentAlignment.MiddleLeft;
        _capStatus.Text = "reading...";
        _host.Controls.Add(_capStatus);

        var tip = new ToolTip();
        tip.SetToolTip(_capCombo,
            "CPU max processor state via powercfg, applied to AC and DC.\r\n" +
            "99% disables Turbo — the main thermal driver on the 5800H.\r\n" +
            "Stored per power scheme: switching plans drops the cap.");
    }

    /// <summary>
    /// Re-reads the cap off the active scheme. Worth doing every time the
    /// window opens, since the cap lives per power scheme and the user may
    /// have switched plans behind our back.
    /// </summary>
    public void RefreshCap()
    {
        if (!IsHandleCreated) return;   // nothing is visible yet; OnHandleCreated will call us
        _ = Task.Run(() =>
        {
            var (ac, dc) = CpuCap.Read();
            try { BeginInvoke(() => ShowCap(ac, dc)); }
            catch (ObjectDisposedException) { }
            catch (InvalidOperationException) { }
        });
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        RefreshCap();
        RefreshPowerState();
        RefreshAmdState();
        RefreshProfileState();
    }

    private void ShowCap(int? ac, int? dc)
    {
        _capStatus.Text = _settings.LockCpuBothRails
            ? "locked to 100%"
            : CpuCap.Describe(ac, dc);

        int? current = ac ?? dc;
        if (current is null) return;

        _suppressCapEvents = true;
        try
        {
            // A scheme can hold a value that is not on our list (set elsewhere).
            // Show it rather than silently snapping to something we did not set.
            if (!_capCombo.Items.Contains(current.Value)) _capCombo.Items.Add(current.Value);
            _capCombo.SelectedItem = current.Value;
        }
        finally { _suppressCapEvents = false; }
    }

    /// <summary>
    /// The CPU cap dropdown writes PROCTHROTTLEMAX; the "lock CPU to 100%"
    /// switch pins the same setting. Both live means the last one clicked wins
    /// and the UI lies about the other, so the dropdown yields to the switch.
    /// </summary>
    private void UpdateCapEnabled()
    {
        bool locked = _settings.LockCpuBothRails;
        _capCombo.Enabled = !_busy && !locked;
        if (locked) _capStatus.Text = "locked to 100%";
    }

    private async Task OnCapChangedAsync()
    {
        if (_suppressCapEvents || _busy) return;
        if (_capCombo.SelectedItem is not int percent) return;

        await RunWriteAsync(() => Task.Run(() => CpuCap.Apply(percent)));
        RefreshCap();
    }

    private void AddLabel(string text, int x, int y, int w)
    {
        var lab = new Label
        {
            Text = text,
            ForeColor = Dim,
            AutoSize = false,
            TextAlign = ContentAlignment.MiddleLeft,
        };
        lab.SetBounds(x, y, w, 24);
        _host.Controls.Add(lab);
    }

    /// <summary>
    /// DropDownList style: the value can only ever come from the list, so no
    /// clamping of typed input is needed.
    /// </summary>
    private void ConfigureCombo(ComboBox combo, int[] values, int current, int x, int y)
    {
        combo.DropDownStyle = ComboBoxStyle.DropDownList;
        combo.FlatStyle = FlatStyle.Standard;
        combo.BackColor = Color.White;
        combo.ForeColor = Fg;
        combo.SetBounds(x, y, 58, 24);
        foreach (int v in values) combo.Items.Add(v);
        SelectValue(combo, current, values);
        combo.SelectedIndexChanged += (_, _) => OnThresholdChanged();
        _host.Controls.Add(combo);
    }

    private static void SelectValue(ComboBox combo, int value, int[] values)
    {
        int idx = Array.IndexOf(values, value);
        combo.SelectedIndex = idx >= 0 ? idx : 0;
    }

    private void OnThresholdChanged()
    {
        if (_suppressThresholdEvents) return;

        _settings.HighThresholdC = (int)(_highCombo.SelectedItem ?? _settings.HighThresholdC);
        _settings.LowThresholdC = _settings.HighThresholdC - ReleaseOffsetC;
        _settings.Clamp();

        _suppressThresholdEvents = true;
        try { SelectValue(_highCombo, _settings.HighThresholdC, EngageValues); }
        finally { _suppressThresholdEvents = false; }

        ShowReleasePoint();
        _settings.Save();
    }

    private void ShowReleasePoint() =>
        _releaseLabel.Text = "°C  ·  release at " + _settings.LowThresholdC + " °C";

    /// <summary>Raised when the auto-max toggle is flipped from this window.</summary>
    public event Action? AutoMaxChanged;

    private void ApplyViewMode()
    {
        bool gauge = _settings.ViewMode == ViewMode.Gauge;
        _gaugePanel.Visible = gauge;
        _numbersPanel.Visible = !gauge;
        _viewButton.Text = gauge ? "Numbers" : "Gauges";
    }

    private void UpdateAutoMaxButton()
    {
        bool on = _settings.AutoMaxEnabled;
        _autoMaxButton.Text = on ? "Auto-max: ON" : "Auto-max: off";
        _autoMaxButton.BackColor = on ? AccentGreen : ButtonFace;
        _autoMaxButton.ForeColor = on ? Color.White : Fg;
        _autoMaxButton.FlatAppearance.BorderColor = on ? AccentGreen : ButtonBorder;
    }


    // --- GPU / Power tab ----------------------------------------------------

    /// <summary>
    /// Tab 1 is the dashboard exactly as it was; tab 2 is everything added
    /// here. The alternative -- one long page -- would have pushed the window
    /// past the height of the screen corner it lives in.
    /// </summary>
    private void BuildTabs()
    {
        _tabs.Dock = DockStyle.Fill;
        _fansPage.BackColor = Bg;
        _gpuPage.BackColor = Bg;
        _amdPage.BackColor = Bg;
        _tabs.TabPages.Add(_fansPage);
        _tabs.TabPages.Add(_gpuPage);
        _tabs.TabPages.Add(_amdPage);
        Controls.Add(_tabs);
    }

    private void BuildGpuTab()
    {
        BuildNvidiaRow();
        BuildPowerSwitches();
        BuildGpuOverrides();

        _gpuResult.SetBounds(12, 442, 304, 46);
        _gpuResult.ForeColor = Dim;
        _gpuResult.AutoSize = false;
        _host.Controls.Add(_gpuResult);
    }

    /// <summary>
    /// The only row on this tab the app does not automate. See NvidiaHints for
    /// why Battery Boost is guidance rather than a switch.
    /// </summary>
    private void BuildNvidiaRow()
    {
        AddHeading("NVIDIA frame-rate cap", 12, 6, 304);

        _nvStatus.SetBounds(12, 26, 304, 34);
        _nvStatus.ForeColor = Dim;
        _nvStatus.AutoSize = false;
        _nvStatus.Text = NvidiaHints.Describe(true);
        _host.Controls.Add(_nvStatus);

        var open = StyleButton(new Button(), "Open NVIDIA app", 12, 62, 148, 28);
        open.Click += async (_, _) => await RunWriteAsync(() => Task.FromResult(NvidiaHints.LaunchNvidiaApp()));

        var win = StyleButton(new Button(), "Graphics settings", 168, 62, 148, 28);
        win.Click += async (_, _) => await RunWriteAsync(() => Task.FromResult(NvidiaHints.OpenWindowsGraphicsSettings()));

        var tip = new ToolTip();
        tip.SetToolTip(_nvStatus, NvidiaHints.Steps);
        tip.SetToolTip(open, NvidiaHints.Steps);
    }

    private void BuildPowerSwitches()
    {
        for (int i = 0; i < PowerRows.Length; i++)
        {
            int index = i;   // captured per row, not per loop
            var sw = _powerSwitches[i];
            StyleSwitch(sw, PowerRows[i].Toggle.Caption, 12, 98 + i * 40, 304, 38);
            sw.CheckedChanged += async (_, _) => await OnPowerSwitchAsync(index);

            var tip = new ToolTip();
            tip.SetToolTip(sw, PowerRows[i].Toggle.Tooltip);
        }
    }

    /// <summary>
    /// A list of executables, with no application singled out: Windows keys
    /// this preference on a full path, so a game, an engine, a renderer and a
    /// browser all belong here on equal footing.
    /// </summary>
    private void BuildGpuOverrides()
    {
        AddHeading("Per-app GPU override", 12, 220, 304);

        StyleSwitch(_gpuSwitch, "Apply the overrides below", 12, 240, 304, 38);
        _gpuSwitch.CheckedChanged += async (_, _) => await OnGpuSwitchAsync();
        new ToolTip().SetToolTip(_gpuSwitch,
            "Writes HKCU\\Software\\Microsoft\\DirectX\\UserGpuPreferences,\r\n" +
            "the registry behind Settings -> System -> Display -> Graphics.\r\n" +
            "Off clears every entry back to \"Let Windows decide\".");

        _appList.SetBounds(12, 282, 304, 120);
        _appList.BackColor = Color.White;
        _appList.ForeColor = Fg;
        _appList.BorderStyle = BorderStyle.FixedSingle;
        _appList.IntegralHeight = false;
        _appList.SelectedIndexChanged += (_, _) => ShowSelectedPreference();
        _host.Controls.Add(_appList);

        AddLabel("Use", 12, 410, 28);

        _prefCombo.DropDownStyle = ComboBoxStyle.DropDownList;
        _prefCombo.FlatStyle = FlatStyle.Standard;
        _prefCombo.BackColor = Color.White;
        _prefCombo.ForeColor = Fg;
        _prefCombo.SetBounds(44, 410, 110, 24);
        _prefCombo.Items.Add(GpuPreference.Describe(GpuPreference.PowerSaving));
        _prefCombo.Items.Add(GpuPreference.Describe(GpuPreference.HighPerf));
        _prefCombo.SelectedIndexChanged += async (_, _) => await OnPreferenceChangedAsync();
        _host.Controls.Add(_prefCombo);

        StyleButton(_addAppButton, "Add...", 162, 409, 72, 26);
        _addAppButton.Click += async (_, _) => await OnAddAppAsync();

        StyleButton(_removeAppButton, "Remove", 240, 409, 76, 26);
        _removeAppButton.Click += async (_, _) => await OnRemoveAppAsync();
    }

    private void AddHeading(string text, int x, int y, int w)
    {
        var lab = new Label
        {
            Text = text,
            ForeColor = Fg,
            Font = new Font(Font, FontStyle.Bold),
            AutoSize = false,
            TextAlign = ContentAlignment.MiddleLeft,
        };
        lab.SetBounds(x, y, w, 20);
        _host.Controls.Add(lab);
    }

    private void StyleSwitch(ToggleSwitch sw, string caption, int x, int y, int w, int h)
    {
        sw.Caption = caption;
        sw.SetBounds(x, y, w, h);
        sw.BackColor = Bg;
        sw.ForeColor = Fg;
        sw.OnColor = AccentGreen;
        sw.BorderColor = ButtonBorder;
        sw.SubColor = Dim;
        _host.Controls.Add(sw);
    }

    // --- GPU / Power behaviour ----------------------------------------------

    private async Task OnPowerSwitchAsync(int index)
    {
        var row = PowerRows[index];
        var sw = _powerSwitches[index];
        bool want = sw.Checked;

        bool ok = await RunWriteAsync(() => Task.Run(() =>
            want ? PowerPlan.Engage(row.Toggle, _settings) : PowerPlan.Release(row.Toggle, _settings)));

        // Save either way: an engage that failed halfway has still captured the
        // original values, and losing those would strand the machine.
        if (ok) row.Set(_settings, want);
        _settings.Save();

        if (!ok) sw.SetCheckedSilently(!want);
        UpdateCapEnabled();
        RefreshPowerState();
    }

    private async Task OnGpuSwitchAsync()
    {
        bool want = _gpuSwitch.Checked;
        bool ok = await RunWriteAsync(() => Task.Run(() =>
            want ? GpuPreference.ApplyAll(_settings.GpuOverrides) : GpuPreference.ClearAll(_settings.GpuOverrides)));

        if (ok)
        {
            _settings.GpuOverridesEnabled = want;
            _settings.Save();
        }
        else
        {
            _gpuSwitch.SetCheckedSilently(!want);
        }
        RefreshAppList();
    }

    private async Task OnAddAppAsync()
    {
        using var dlg = new OpenFileDialog
        {
            Title = "Choose an executable",
            Filter = "Programs (*.exe)|*.exe|All files (*.*)|*.*",
            CheckFileExists = true,
        };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        await AddPathsAsync(new[] { dlg.FileName });
    }

    /// <summary>
    /// Adds paths not already listed, defaulting them to the integrated GPU --
    /// which, while a battery FPS cap is in force, is the faster of the two.
    /// </summary>
    private async Task AddPathsAsync(IEnumerable<string> paths)
    {
        var added = new List<GpuOverride>();
        foreach (string path in paths)
        {
            if (_settings.GpuOverrides.Any(o => string.Equals(o.Path, path, StringComparison.OrdinalIgnoreCase))) continue;
            var entry = new GpuOverride { Path = path, Preference = GpuPreference.PowerSaving };
            _settings.GpuOverrides.Add(entry);
            added.Add(entry);
        }

        if (added.Count == 0)
        {
            _gpuResult.ForeColor = Dim;
            _gpuResult.Text = "Already in the list.";
            return;
        }

        _settings.Save();
        RefreshAppList();
        SelectPath(added[0].Path);

        if (_settings.GpuOverridesEnabled)
        {
            await RunWriteAsync(() => Task.Run(() => GpuPreference.ApplyAll(added)));
            RefreshAppList();
        }
    }

    private async Task OnRemoveAppAsync()
    {
        var entry = SelectedOverride();
        if (entry is null) return;

        // Clear the registry value even when the master switch is off: the
        // entry may have been applied in an earlier session, and dropping it
        // from the list without clearing would orphan the override.
        await RunWriteAsync(() => Task.Run(() => GpuPreference.Clear(entry.Path)));

        _settings.GpuOverrides.Remove(entry);
        _settings.Save();
        RefreshAppList();
    }

    private async Task OnPreferenceChangedAsync()
    {
        if (_suppressAppEvents) return;
        var entry = SelectedOverride();
        if (entry is null) return;

        int pref = _prefCombo.SelectedIndex == 1 ? GpuPreference.HighPerf : GpuPreference.PowerSaving;
        if (pref == entry.Preference) return;

        entry.Preference = pref;
        _settings.Save();

        if (_settings.GpuOverridesEnabled)
            await RunWriteAsync(() => Task.Run(() => GpuPreference.Apply(entry.Path, entry.Preference)));

        RefreshAppList();
        SelectPath(entry.Path);
    }

    private GpuOverride? SelectedOverride()
    {
        int i = _appList.SelectedIndex;
        return i >= 0 && i < _settings.GpuOverrides.Count ? _settings.GpuOverrides[i] : null;
    }

    private void SelectPath(string path)
    {
        int i = _settings.GpuOverrides.FindIndex(o => string.Equals(o.Path, path, StringComparison.OrdinalIgnoreCase));
        if (i >= 0) _appList.SelectedIndex = i;
    }

    private void ShowSelectedPreference()
    {
        var entry = SelectedOverride();
        _suppressAppEvents = true;
        try { _prefCombo.SelectedIndex = entry?.Preference == GpuPreference.HighPerf ? 1 : 0; }
        finally { _suppressAppEvents = false; }

        // With nothing selected the combo would sit on its first item and imply
        // a preference that belongs to no entry.
        _prefCombo.Enabled = !_busy && entry is not null;
        _removeAppButton.Enabled = !_busy && entry is not null;
    }

    /// <summary>Pills and list rebuilt from settings, without raising writes.</summary>
    private void SyncGpuTabFromSettings()
    {
        for (int i = 0; i < PowerRows.Length; i++)
            _powerSwitches[i].SetCheckedSilently(PowerRows[i].Get(_settings));
        _gpuSwitch.SetCheckedSilently(_settings.GpuOverridesEnabled);
        RefreshAppList();
        UpdateCapEnabled();
    }

    /// <summary>
    /// Row text carries the live registry state, not just what we intend, so a
    /// value someone changed in Windows Settings is visible as a mismatch
    /// rather than being quietly misreported.
    /// </summary>
    private void RefreshAppList()
    {
        _suppressAppEvents = true;
        try
        {
            int keep = _appList.SelectedIndex;
            _appList.BeginUpdate();
            _appList.Items.Clear();
            foreach (var o in _settings.GpuOverrides)
            {
                int? live = GpuPreference.Read(o.Path);
                string state = live is null ? "not applied"
                    : live == o.Preference ? "applied"
                    : "Windows says " + GpuPreference.Describe(live.Value);
                _appList.Items.Add(Path.GetFileName(o.Path) + "  -  " + GpuPreference.Describe(o.Preference) + "  ·  " + state);
            }
            _appList.EndUpdate();
            if (keep >= 0 && keep < _appList.Items.Count) _appList.SelectedIndex = keep;
        }
        finally { _suppressAppEvents = false; }

        _gpuSwitch.SubCaption = _settings.GpuOverrides.Count == 0
            ? "no apps listed"
            : _settings.GpuOverrides.Count + " app" + (_settings.GpuOverrides.Count == 1 ? "" : "s") + " listed";

        ShowSelectedPreference();
    }

    /// <summary>
    /// Re-reads the three powercfg settings off the UI thread, and folds in any
    /// GPU override configured outside this app. Worth doing on every open for
    /// the same reason as RefreshCap: these values live per power scheme, so
    /// switching plans silently drops them.
    /// </summary>
    public void RefreshPowerState()
    {
        if (!IsHandleCreated) return;
        _ = Task.Run(() =>
        {
            var values = PowerRows.Select(r => PowerPlan.Read(r.Toggle)).ToArray();
            var known = GpuPreference.ReadAll();
            try { BeginInvoke(() => ShowPowerState(values, known)); }
            catch (ObjectDisposedException) { }
            catch (InvalidOperationException) { }
        });
    }

    private void ShowPowerState((int? Ac, int? Dc)[][] values, List<(string Path, int Preference)> known)
    {
        for (int i = 0; i < PowerRows.Length; i++)
        {
            var toggle = PowerRows[i].Toggle;

            // A setting this Windows build does not have gets a disabled row
            // saying so, rather than a pill that silently never takes.
            // SUB_ENERGYSAVER is absent from every scheme on Windows 10 19045.
            bool supported = values[i].Any(v => v.Ac is not null || v.Dc is not null);
            _powerSwitches[i].Enabled = supported;
            if (!supported)
            {
                _powerSwitches[i].SubCaption = "not available on this Windows build";
                _powerSwitches[i].SetCheckedSilently(false);
                continue;
            }

            _powerSwitches[i].SubCaption = toggle.Describe(values[i]);

            // Believe the machine over the stored flag: the scheme may have been
            // changed behind our back, and a pill that disagrees with powercfg
            // is worse than no pill at all.
            bool live = PowerPlan.IsEngaged(toggle, values[i]);
            _powerSwitches[i].SetCheckedSilently(live);
            if (PowerRows[i].Get(_settings) != live)
            {
                PowerRows[i].Set(_settings, live);
                _settings.Save();
            }
        }
        UpdateCapEnabled();

        // Apps configured in Windows Settings become visible here rather than
        // being silently shadowed by whatever this list happens to hold.
        bool grew = false;
        foreach (var (path, preference) in known)
        {
            if (_settings.GpuOverrides.Any(o => string.Equals(o.Path, path, StringComparison.OrdinalIgnoreCase))) continue;
            _settings.GpuOverrides.Add(new GpuOverride { Path = path, Preference = preference });
            grew = true;
        }
        if (grew) _settings.Save();
        RefreshAppList();
    }


    // --- AMD tab ------------------------------------------------------------

    /// <summary>
    /// The AMD counterparts to the NVIDIA settings the app will not touch. They
    /// earn their own page rather than a fourth block on the GPU tab: that page
    /// is already full to its bottom edge, and these write the display driver's
    /// registry key rather than powercfg.
    ///
    /// No settings.json flags here. The driver key is the single source of
    /// truth and Adrenalin writes it too, so the switches are read from the
    /// machine on every open rather than remembered.
    /// </summary>
    private void BuildAmdTab()
    {
        AddHeading("AMD Radeon driver settings", 12, 6, 304);

        _amdStatus.SetBounds(12, 26, 304, 34);
        _amdStatus.ForeColor = Dim;
        _amdStatus.AutoSize = false;
        _host.Controls.Add(_amdStatus);

        // FRTC first: it is the one that behaves like Battery Boost.
        StyleSwitch(_amdSwitches[0], AmdGpu.All[0].Caption, 12, 64, 304, 38);
        new ToolTip().SetToolTip(_amdSwitches[0], AmdGpu.All[0].Tooltip);
        _amdSwitches[0].CheckedChanged += async (_, _) => await OnFrtcSwitchAsync();

        AddLabel("Cap at", 12, 104, 46);
        _fpsCombo.DropDownStyle = ComboBoxStyle.DropDownList;
        _fpsCombo.FlatStyle = FlatStyle.Standard;
        _fpsCombo.BackColor = Color.White;
        _fpsCombo.ForeColor = Fg;
        _fpsCombo.SetBounds(62, 104, 74, 24);
        foreach (int v in AmdGpu.FpsValues) _fpsCombo.Items.Add(v);
        _fpsCombo.SelectedIndexChanged += async (_, _) => await OnFpsChangedAsync();
        _host.Controls.Add(_fpsCombo);
        AddLabel("FPS", 142, 104, 34);

        for (int i = 1; i < AmdGpu.All.Length; i++)
        {
            int index = i;
            var sw = _amdSwitches[i];
            StyleSwitch(sw, AmdGpu.All[i].Caption, 12, 100 + i * 40, 304, 38);
            new ToolTip().SetToolTip(sw, AmdGpu.All[i].Tooltip);
            sw.CheckedChanged += async (_, _) => await OnAmdSwitchAsync(index);
        }

        var note = new Label
        {
            Text = "Global, not per-app: AMD's per-application profiles live in a "
                 + "binary database with no supported API. Changes apply the next "
                 + "time the affected app starts.",
            ForeColor = Dim,
            AutoSize = false,
        };
        note.SetBounds(12, 306, 304, 58);
        _host.Controls.Add(note);

        var open = StyleButton(new Button(), "Open AMD Software", 12, 370, 160, 28);
        open.Click += async (_, _) => await RunWriteAsync(() => Task.FromResult(AmdHints.LaunchAdrenalin()));

        _amdResult.SetBounds(12, 406, 304, 82);
        _amdResult.ForeColor = Dim;
        _amdResult.AutoSize = false;
        _host.Controls.Add(_amdResult);
    }

    private async Task OnFrtcSwitchAsync()
    {
        if (_suppressAmdEvents) return;
        var sw = _amdSwitches[0];
        bool want = sw.Checked;
        int fps = _fpsCombo.SelectedItem as int? ?? AmdGpu.ReadFrtcFps() ?? 60;

        bool ok = await RunWriteAsync(() => Task.Run(() => AmdGpu.ApplyFrtc(want, fps)));
        if (!ok) sw.SetCheckedSilently(!want);
        RefreshAmdState();
    }

    private async Task OnFpsChangedAsync()
    {
        if (_suppressAmdEvents) return;
        if (_fpsCombo.SelectedItem is not int fps) return;

        // Only meaningful while the cap is on; with it off this just stages the
        // rate for the next time it is turned on.
        if (!_amdSwitches[0].Checked)
        {
            await RunWriteAsync(() => Task.Run(() => AmdGpu.Write(AmdGpu.FrtcMaxFps, fps)));
        }
        else
        {
            await RunWriteAsync(() => Task.Run(() => AmdGpu.ApplyFrtc(true, fps)));
        }
        RefreshAmdState();
    }

    private async Task OnAmdSwitchAsync(int index)
    {
        if (_suppressAmdEvents) return;
        var toggle = AmdGpu.All[index];
        var sw = _amdSwitches[index];
        bool want = sw.Checked;

        bool ok = await RunWriteAsync(() => Task.Run(() => AmdGpu.Write(toggle, want)));
        if (!ok) sw.SetCheckedSilently(!want);
        RefreshAmdState();
    }

    /// <summary>
    /// Re-reads the driver key off the UI thread. Adrenalin rewrites these
    /// values when its own UI runs, so what we wrote last is not evidence of
    /// what is in force now.
    /// </summary>
    public void RefreshAmdState()
    {
        if (!IsHandleCreated) return;
        _ = Task.Run(() =>
        {
            string? adapter = AmdGpu.AdapterName();
            var values = AmdGpu.All.Select(AmdGpu.Read).ToArray();
            int? fps = AmdGpu.ReadFrtcFps();
            try { BeginInvoke(() => ShowAmdState(adapter, values, fps)); }
            catch (ObjectDisposedException) { }
            catch (InvalidOperationException) { }
        });
    }

    private void ShowAmdState(string? adapter, int?[] values, int? fps)
    {
        bool present = adapter is not null;
        _amdStatus.Text = present
            ? adapter + "  ·  driver key found"
            : "No AMD display adapter found. Nothing on this page applies.";
        _amdStatus.ForeColor = present ? Dim : WarnText;

        _suppressAmdEvents = true;
        try
        {
            for (int i = 0; i < AmdGpu.All.Length; i++)
            {
                var toggle = AmdGpu.All[i];
                _amdSwitches[i].Enabled = present && values[i] is not null;
                _amdSwitches[i].SetCheckedSilently(toggle.IsOn(values[i]));
                _amdSwitches[i].SubCaption = values[i] is null
                    ? toggle.ValueName + " not set"
                    : toggle.ValueName + " = " + values[i];
            }

            if (fps is not null && !_fpsCombo.Items.Contains(fps.Value)) _fpsCombo.Items.Add(fps.Value);
            _fpsCombo.SelectedItem = fps;
        }
        finally { _suppressAmdEvents = false; }

        // The rate only matters while the cap is on.
        _fpsCombo.Enabled = !_busy && present && AmdGpu.Frtc.IsOn(values[0]);
    }


    // --- profile buttons ----------------------------------------------------

    /// <summary>
    /// One click for everything the GPU / Power and AMD tabs expose as nine
    /// separate switches. Same geometry as the Quiet / Balanced / Performance
    /// row at y=336, so the two read as siblings -- that row owns the fans,
    /// this one owns everything else, and neither touches the other.
    /// </summary>
    private void BuildProfiles()
    {
        _profileHeading.SetBounds(16, 496, 288, 20);
        _profileHeading.ForeColor = Fg;
        _profileHeading.Font = new Font(Font, FontStyle.Bold);
        _profileHeading.AutoSize = false;
        _profileHeading.UseMnemonic = false;
        _profileHeading.TextAlign = ContentAlignment.MiddleLeft;
        _profileHeading.Text = "GPU & power profile";
        _host.Controls.Add(_profileHeading);

        int[] x = { 16, 114, 212 };
        for (int i = 0; i < PowerProfile.All.Length; i++)
        {
            int index = i;
            var profile = PowerProfile.All[i];
            StyleButton(_profileButtons[i], profile.Name, x[i], 516, 92, 28);
            _profileButtons[i].Click += async (_, _) => await OnProfileAsync(index);
            new ToolTip().SetToolTip(_profileButtons[i], profile.Tooltip);
        }

        StyleButton(_restoreButton, "Restore original", 16, 550, 140, 26);
        _restoreButton.Enabled = false;
        _restoreButton.Click += async (_, _) => await OnRestoreAsync();
        new ToolTip().SetToolTip(_restoreButton,
            "Puts every setting back to how the machine was before the first\r\n" +
            "profile was applied. Captured once, never overwritten.");

        _snapshotLabel.SetBounds(162, 550, 142, 26);
        _snapshotLabel.ForeColor = Dim;
        _snapshotLabel.AutoSize = false;
        _snapshotLabel.TextAlign = ContentAlignment.MiddleLeft;
        _host.Controls.Add(_snapshotLabel);

        ShowSnapshotAge();
    }

    private async Task OnProfileAsync(int index)
    {
        var profile = PowerProfile.All[index];

        bool ok = await RunWriteAsync(() => Task.Run(() =>
        {
            // Captured before the first write of the first profile, so there is
            // always a way back to how the laptop shipped.
            _settings.OriginalState ??= MachineSnapshot.Capture(_settings);
            return profile.Apply(_settings);
        }));

        // Saved either way: a profile that failed halfway still moved settings,
        // and the snapshot it captured is the only record of what was there.
        _settings.Save();
        _ = ok;

        RefreshEverything();
    }

    private async Task OnRestoreAsync()
    {
        var snapshot = _settings.OriginalState;
        if (snapshot is null) return;

        await RunWriteAsync(() => Task.Run(() => snapshot.Restore(_settings)));
        _settings.Save();
        RefreshEverything();
    }

    /// <summary>Every tab re-reads after a profile: one click moved all of them.</summary>
    public void RefreshEverything()
    {
        // SyncGpuTabFromSettings first: a profile can flip GpuOverridesEnabled
        // and rewrite each entry's preference, and the pill and list have to
        // follow. RefreshPowerState lands afterwards and corrects the powercfg
        // switches against the machine, which is the authority for those.
        SyncGpuTabFromSettings();
        RefreshCap();
        RefreshPowerState();
        RefreshAmdState();
        RefreshProfileState();
        ShowSnapshotAge();
    }

    private void ShowSnapshotAge()
    {
        var snapshot = _settings.OriginalState;
        _snapshotLabel.Text = snapshot is null
            ? "nothing captured yet"
            : "captured " + snapshot.CapturedUtc.ToLocalTime().ToString("d MMM HH:mm");
        _restoreButton.Enabled = !_busy && snapshot is not null;
    }

    /// <summary>
    /// Reads the whole machine off the UI thread and highlights whichever
    /// profile it currently matches. Same shape and the same disposal guards as
    /// RefreshPowerState.
    /// </summary>
    public void RefreshProfileState()
    {
        if (!IsHandleCreated) return;
        _ = Task.Run(() =>
        {
            var live = MachineSnapshot.Capture(_settings);
            var active = PowerProfile.Active(live, _settings);
            try { BeginInvoke(() => ShowActiveProfile(active)); }
            catch (ObjectDisposedException) { }
            catch (InvalidOperationException) { }
        });
    }

    private void ShowActiveProfile(PowerProfile? active)
    {
        // Named as well as tinted: a green button says which one only if you
        // already know the three by sight, and the name survives a screenshot.
        _profileHeading.Text = "GPU & power profile  ·  " + (active?.Name ?? "Custom");
        _profileHeading.ForeColor = active is null ? Dim : OkText;

        for (int i = 0; i < _profileButtons.Length; i++)
        {
            bool on = ReferenceEquals(PowerProfile.All[i], active);
            _profileButtons[i].BackColor = on ? AccentGreen : ButtonFace;
            _profileButtons[i].ForeColor = on ? Color.White : Fg;
            _profileButtons[i].FlatAppearance.BorderColor = on ? AccentGreen : ButtonBorder;
        }

        ActiveProfileChanged?.Invoke(active);
    }

    /// <summary>Raised so the tray menu can tick the same profile the window highlights.</summary>
    public event Action<PowerProfile?>? ActiveProfileChanged;

    // --- live update --------------------------------------------------------

    public void Apply(FanSnapshot s)
    {
        _cpuGauge.Value = s.CpuC;
        _gpuGauge.Value = s.GpuC;
        _cpuBig.Text = s.CpuC.HasValue ? s.CpuC.Value + "°" : "n/a";
        _gpuBig.Text = s.GpuC.HasValue ? s.GpuC.Value + "°" : "n/a";

        _fan0.Maximum = s.MaxRpm;
        _fan1.Maximum = s.MaxRpm;
        _fan0.Value = s.Fan0Rpm;
        _fan1.Value = s.Fan1Rpm;

        string owner = s.Owner switch
        {
            FanOwner.Manual => "  ·  set manually",
            FanOwner.Auto => "  ·  set by auto-max",
            _ => "",
        };
        _status.Text = s.ModeText + "  ·  " + (s.OnAc ? "AC" : "Battery") + owner;
        _status.ForeColor = s.FansPinned ? WarnText : Fg;

        _nvStatus.Text = NvidiaHints.Describe(s.OnAc);
        _nvStatus.ForeColor = s.OnAc ? Dim : WarnText;

        UpdateAutoMaxButton();
        _releaseButton.Enabled = !_busy && s.CustomEngaged;
        _maxButton.Enabled = !_busy;
    }

    /// <summary>
    /// Returns whether the write succeeded, so a switch row can roll its pill
    /// back when the machine refused. Callers that only wanted the status line
    /// can keep ignoring the result.
    /// </summary>
    private async Task<bool> RunWriteAsync(Func<Task<WriteResult>> action)
    {
        if (_busy) return false;
        SetBusy(true, "Writing, then verifying by readback...");
        try
        {
            var r = await action();
            _result.ForeColor = r.Ok ? OkText : ErrText;
            _result.Text = r.Message;
            _gpuResult.ForeColor = _result.ForeColor;
            _gpuResult.Text = r.Message;
            _amdResult.ForeColor = _result.ForeColor;
            _amdResult.Text = r.Message;
            return r.Ok;
        }
        catch (Exception ex)
        {
            _result.ForeColor = ErrText;
            _result.Text = ex.Message;
            _gpuResult.ForeColor = ErrText;
            _gpuResult.Text = ex.Message;
            _amdResult.ForeColor = ErrText;
            _amdResult.Text = ex.Message;
            return false;
        }
        finally
        {
            SetBusy(false, null);
        }
    }

    private void SetBusy(bool busy, string? message)
    {
        _busy = busy;
        foreach (var b in new[] { _maxButton, _releaseButton, _quietButton, _balancedButton, _perfButton,
                                  _addAppButton, _removeAppButton }.Concat(_profileButtons))
            b.Enabled = !busy;
        _restoreButton.Enabled = !busy && _settings.OriginalState is not null;
        _prefCombo.Enabled = !busy;
        _appList.Enabled = !busy;
        foreach (var sw in _powerSwitches) sw.Busy = busy;
        foreach (var sw in _amdSwitches) sw.Busy = busy;
        _gpuSwitch.Busy = busy;
        _fpsCombo.Enabled = !busy && AmdGpu.Frtc.IsOn(AmdGpu.Read(AmdGpu.Frtc));
        UpdateCapEnabled();

        if (message != null)
        {
            _result.ForeColor = Dim;
            _result.Text = message;
            _gpuResult.ForeColor = Dim;
            _gpuResult.Text = message;
            _amdResult.ForeColor = Dim;
            _amdResult.Text = message;
        }
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        // Closing the window must not release the fans or kill the tray.
        if (e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            Hide();
        }
        base.OnFormClosing(e);
    }
}

