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
    private readonly Button _viewButton = new();

    private readonly ComboBox _highCombo = new();
    private readonly Label _releaseLabel = new();
    private readonly ComboBox _capCombo = new();
    private readonly Label _capStatus = new();

    private bool _busy;
    private bool _suppressThresholdEvents;
    private bool _suppressCapEvents;

    public DashboardForm(FanController controller, Settings settings)
    {
        _controller = controller;
        _settings = settings;

        Text = AppVersion.Title;
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        ClientSize = new Size(320, 488);
        BackColor = Bg;
        ForeColor = Fg;
        Font = new Font("Segoe UI", 9f);

        // Derived, not stored-and-trusted: a hand-edited settings.json could
        // otherwise show a release point the single dropdown cannot express.
        _settings.LowThresholdC = _settings.HighThresholdC - ReleaseOffsetC;
        _settings.Clamp();

        BuildGauges();
        BuildNumbers();
        BuildBars();
        BuildStatus();
        BuildButtons();
        BuildThresholds();
        BuildCpuCap();

        ApplyViewMode();
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
        Controls.Add(_gaugePanel);
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
        Controls.Add(_numbersPanel);
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

        Controls.Add(_fan0);
        Controls.Add(_fan1);
    }

    private void BuildStatus()
    {
        _status.SetBounds(16, 240, 288, 18);
        _status.ForeColor = Fg;
        _status.AutoSize = false;
        Controls.Add(_status);

        _result.SetBounds(16, 260, 288, 30);
        _result.ForeColor = Dim;
        _result.AutoSize = false;
        Controls.Add(_result);
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
        Controls.Add(b);
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
        Controls.Add(_releaseLabel);
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
        Controls.Add(_capCombo);

        AddLabel("%", 136, 438, 14);

        _capStatus.SetBounds(156, 438, 148, 24);
        _capStatus.ForeColor = Dim;
        _capStatus.AutoSize = false;
        _capStatus.TextAlign = ContentAlignment.MiddleLeft;
        _capStatus.Text = "reading...";
        Controls.Add(_capStatus);

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
    }

    private void ShowCap(int? ac, int? dc)
    {
        _capStatus.Text = CpuCap.Describe(ac, dc);

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
        Controls.Add(lab);
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
        Controls.Add(combo);
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

        UpdateAutoMaxButton();
        _releaseButton.Enabled = !_busy && s.CustomEngaged;
        _maxButton.Enabled = !_busy;
    }

    private async Task RunWriteAsync(Func<Task<WriteResult>> action)
    {
        if (_busy) return;
        SetBusy(true, "Writing, then verifying by readback...");
        try
        {
            var r = await action();
            _result.ForeColor = r.Ok ? OkText : ErrText;
            _result.Text = r.Message;
        }
        catch (Exception ex)
        {
            _result.ForeColor = ErrText;
            _result.Text = ex.Message;
        }
        finally
        {
            SetBusy(false, null);
        }
    }

    private void SetBusy(bool busy, string? message)
    {
        _busy = busy;
        foreach (var b in new[] { _maxButton, _releaseButton, _quietButton, _balancedButton, _perfButton })
            b.Enabled = !busy;
        _capCombo.Enabled = !busy;

        if (message != null)
        {
            _result.ForeColor = Dim;
            _result.Text = message;
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
