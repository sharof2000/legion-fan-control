using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace LegionFanTray;

/// <summary>Tray icon, context menu, and the bridge to the dashboard window.</summary>
internal sealed class TrayApp : ApplicationContext
{
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr handle);

    private readonly FanController _controller;
    private readonly Settings _settings;
    private readonly NotifyIcon _icon;
    private readonly Control _marshal = new();
    private readonly DashboardForm _dashboard;

    private readonly ToolStripMenuItem _miMax = new("Fans: MAX");
    private readonly ToolStripMenuItem _miRelease = new("Fans: Auto (release)");
    private readonly ToolStripMenuItem _miModeQuiet = new("Quiet");
    private readonly ToolStripMenuItem _miModeBalanced = new("Balanced");
    private readonly ToolStripMenuItem _miModePerformance = new("Performance");
    private readonly ToolStripMenuItem _miAutoMax = new("Auto-max curve");
    private readonly ToolStripMenuItem _miSuspendVantage = new("Suspend Vantage during writes");
    private readonly ToolStripMenuItem _miAutoStart = new("Start with Windows");
    private readonly ToolStripMenuItem _miAutoStartSilent = new("Start without UAC prompt");
    private readonly ToolStripMenuItem _miShowTemp = new("Show temperature in tray icon");

    // GPU / Power tab mirrored into the tray, so the levers you flip around a
    // charger move do not need the dashboard open.
    private readonly ToolStripMenuItem _miLockCpu = new(PowerPlan.LockCpuBothRails.Caption);
    private readonly ToolStripMenuItem _miNoBatterySaver = new(PowerPlan.NeverAutoBatterySaver.Caption);
    private readonly ToolStripMenuItem _miPcieFull = new(PowerPlan.PcieFullPowerOnBattery.Caption);
    private readonly ToolStripMenuItem _miGpuOverrides = new("Apply per-app GPU overrides");
    private readonly ToolStripMenuItem[] _miProfiles =
        PowerProfile.All.Select(p => new ToolStripMenuItem(p.Name)).ToArray();
    private readonly ToolStripMenuItem _miRestoreOriginal = new("Restore original");

    // Never disposed: it lives for the process lifetime and is used on every
    // icon repaint. Segoe UI with a fallback for the rare machine without it.
    private static readonly FontFamily IconFamily = LoadIconFamily();

    private IntPtr _iconHandle = IntPtr.Zero;
    private string _lastIconText = "";
    private bool _exiting;

    public TrayApp(FanController controller, Settings settings)
    {
        _controller = controller;
        _settings = settings;

        bool firstRun = !File.Exists(AppPaths.SettingsFile);

        // NotifyIcon is not a Control, so it gives us no Invoke target. This
        // hidden control is what marshals poll-thread events onto the UI thread.
        _ = _marshal.Handle;

        _dashboard = new DashboardForm(controller, settings);
        _dashboard.AutoMaxChanged += () =>
        {
            _miAutoMax.Checked = _settings.AutoMaxEnabled;
        };

        // The window works out which profile is in force by reading the machine.
        // Ticking the same item here means the two never disagree, and saves
        // doing that read twice.
        _dashboard.ActiveProfileChanged += active =>
        {
            for (int i = 0; i < _miProfiles.Length; i++)
                _miProfiles[i].Checked = ReferenceEquals(PowerProfile.All[i], active);
        };

        _icon = new NotifyIcon
        {
            Icon = SystemIcons.Application,
            Visible = true,
            Text = "Legion Fan Tray",
            ContextMenuStrip = BuildMenu(),
        };
        _icon.DoubleClick += (_, _) => ShowDashboard();

        _controller.SnapshotUpdated += OnSnapshot;
        _controller.Notice += OnNotice;

        SyncMenuFromSettings();

        if (settings.LoadWarning != null)
            Balloon("Settings", settings.LoadWarning, ToolTipIcon.Warning);

        if (firstRun)
        {
            settings.Save();
            Balloon("Legion Fan Tray",
                "Running in the notification area. On Windows 11, drag the icon out of the overflow flyout once to keep it next to the clock.",
                ToolTipIcon.Info);
        }
    }

    // --- menu ---------------------------------------------------------------

    private ContextMenuStrip BuildMenu()
    {
        var menu = new ContextMenuStrip();

        _miMax.Click += async (_, _) => await WriteAsync(() => _controller.EngageMaxAsync(FanOwner.Manual));
        _miRelease.Click += async (_, _) => await WriteAsync(() => _controller.ReleaseAsync(FanOwner.Manual));

        _miModeQuiet.Click += async (_, _) => await WriteAsync(() => _controller.SetModeAsync(LenovoWmi.ModeQuiet));
        _miModeBalanced.Click += async (_, _) => await WriteAsync(() => _controller.SetModeAsync(LenovoWmi.ModeBalanced));
        _miModePerformance.Click += async (_, _) => await WriteAsync(() => _controller.SetModeAsync(LenovoWmi.ModePerformance));

        var modeMenu = new ToolStripMenuItem("Mode");
        modeMenu.DropDownItems.AddRange(new ToolStripItem[] { _miModeQuiet, _miModeBalanced, _miModePerformance });

        _miAutoMax.Click += (_, _) =>
        {
            _settings.AutoMaxEnabled = !_settings.AutoMaxEnabled;
            _settings.Save();
            _miAutoMax.Checked = _settings.AutoMaxEnabled;
        };

        _miSuspendVantage.Click += (_, _) =>
        {
            _settings.SuspendVantage = !_settings.SuspendVantage;
            _settings.Save();
            _miSuspendVantage.Checked = _settings.SuspendVantage;
        };

        _miAutoStart.Click += (_, _) => ToggleAutoStart();
        _miAutoStartSilent.Click += (_, _) => ToggleAutoStartSilent();

        _miShowTemp.Click += (_, _) =>
        {
            _settings.ShowTempInTrayIcon = !_settings.ShowTempInTrayIcon;
            _settings.Save();
            _miShowTemp.Checked = _settings.ShowTempInTrayIcon;
            _lastIconText = "";
            if (_controller.Latest != null) UpdateIcon(_controller.Latest);
        };

        _miLockCpu.Click += (_, _) => TogglePowerAsync(
            PowerPlan.LockCpuBothRails, () => _settings.LockCpuBothRails, v => _settings.LockCpuBothRails = v, _miLockCpu);
        _miNoBatterySaver.Click += (_, _) => TogglePowerAsync(
            PowerPlan.NeverAutoBatterySaver, () => _settings.DisableBatterySaverAuto, v => _settings.DisableBatterySaverAuto = v, _miNoBatterySaver);
        _miPcieFull.Click += (_, _) => TogglePowerAsync(
            PowerPlan.PcieFullPowerOnBattery, () => _settings.KeepPcieFullPower, v => _settings.KeepPcieFullPower = v, _miPcieFull);
        _miGpuOverrides.Click += (_, _) => ToggleGpuOverridesAsync();

        for (int i = 0; i < _miProfiles.Length; i++)
        {
            int index = i;
            _miProfiles[i].Click += (_, _) => ApplyProfileAsync(index);
            _miProfiles[i].ToolTipText = PowerProfile.All[i].Tooltip;
        }
        _miRestoreOriginal.Click += (_, _) => RestoreOriginalAsync();

        var powerMenu = new ToolStripMenuItem("GPU / Power");
        powerMenu.DropDownItems.AddRange(_miProfiles);
        powerMenu.DropDownItems.AddRange(new ToolStripItem[]
        {
            _miRestoreOriginal,
            new ToolStripSeparator(),
            _miLockCpu,
            _miNoBatterySaver,
            _miPcieFull,
            new ToolStripSeparator(),
            _miGpuOverrides,
        });

        var open = new ToolStripMenuItem("Open dashboard");
        open.Click += (_, _) => ShowDashboard();

        var exit = new ToolStripMenuItem("Exit");
        exit.Click += (_, _) => ExitApp();

        menu.Items.AddRange(new ToolStripItem[]
        {
            _miMax,
            _miRelease,
            new ToolStripSeparator(),
            modeMenu,
            _miAutoMax,
            powerMenu,
            new ToolStripSeparator(),
            _miSuspendVantage,
            _miAutoStart,
            _miAutoStartSilent,
            _miShowTemp,
            new ToolStripSeparator(),
            open,
            exit,
        });

        return menu;
    }

    private void SyncMenuFromSettings()
    {
        _miAutoMax.Checked = _settings.AutoMaxEnabled;
        _miSuspendVantage.Checked = _settings.SuspendVantage;
        _miShowTemp.Checked = _settings.ShowTempInTrayIcon;

        // These flags are corrected against powercfg whenever the dashboard
        // refreshes, so the menu follows the machine rather than a stale file.
        _miLockCpu.Checked = _settings.LockCpuBothRails;
        _miNoBatterySaver.Checked = _settings.DisableBatterySaverAuto;
        _miPcieFull.Checked = _settings.KeepPcieFullPower;
        _miGpuOverrides.Checked = _settings.GpuOverridesEnabled;
        _miGpuOverrides.Enabled = _settings.GpuOverrides.Count > 0;
        _miRestoreOriginal.Enabled = _settings.OriginalState is not null;

        // Read the real state of the scheduled task rather than trusting
        // settings.json, which can drift if the task is removed elsewhere.
        bool registered = AutoStart.IsRegistered();
        _settings.AutoStart = registered;
        _settings.AutoStartSilent = registered && AutoStart.IsSilent();
        _miAutoStart.Checked = _settings.AutoStart;
        _miAutoStartSilent.Checked = _settings.AutoStartSilent;
        _miAutoStartSilent.Enabled = _settings.AutoStart;
    }

    /// <summary>
    /// A profile is a dozen powercfg and registry writes, so it runs off the UI
    /// thread like the individual switches do. The whole submenu is disabled
    /// meanwhile: a profile racing a switch would leave neither in force.
    /// </summary>
    private async void ApplyProfileAsync(int index)
    {
        var profile = PowerProfile.All[index];
        SetProfileItemsEnabled(false);
        try
        {
            var r = await Task.Run(() =>
            {
                _settings.OriginalState ??= MachineSnapshot.Capture(_settings);
                return profile.Apply(_settings);
            });

            // Saved either way: a profile that failed halfway still moved
            // settings, and the snapshot it captured is the only record.
            _settings.Save();
            Balloon(profile.Name, r.Message, r.Ok ? ToolTipIcon.Info : ToolTipIcon.Warning);
        }
        finally
        {
            SetProfileItemsEnabled(true);
        }

        SyncMenuFromSettings();
        _dashboard.RefreshEverything();
    }

    private async void RestoreOriginalAsync()
    {
        var snapshot = _settings.OriginalState;
        if (snapshot is null) return;

        SetProfileItemsEnabled(false);
        try
        {
            var r = await Task.Run(() => snapshot.Restore(_settings));
            _settings.Save();
            Balloon("Restore original", r.Message, r.Ok ? ToolTipIcon.Info : ToolTipIcon.Warning);
        }
        finally
        {
            SetProfileItemsEnabled(true);
        }

        SyncMenuFromSettings();
        _dashboard.RefreshEverything();
    }

    private void SetProfileItemsEnabled(bool enabled)
    {
        foreach (var mi in _miProfiles) mi.Enabled = enabled;
        _miRestoreOriginal.Enabled = enabled && _settings.OriginalState is not null;
    }

    /// <summary>
    /// powercfg is slow enough to freeze the menu, so the write runs off the UI
    /// thread. The item is disabled meanwhile: two writes to the same setting
    /// racing each other would leave the captured "before" values wrong.
    /// </summary>
    private async void TogglePowerAsync(PowerToggle toggle, Func<bool> get, Action<bool> set, ToolStripMenuItem item)
    {
        bool want = !get();
        item.Enabled = false;
        try
        {
            var r = await Task.Run(() => want ? PowerPlan.Engage(toggle, _settings) : PowerPlan.Release(toggle, _settings));

            // Save either way: a partial engage has still captured the original
            // values, and losing those would strand the machine.
            if (r.Ok) set(want);
            _settings.Save();
            if (!r.Ok) Balloon(toggle.Caption, r.Message, ToolTipIcon.Warning);
        }
        finally
        {
            item.Enabled = true;
        }

        SyncMenuFromSettings();
        _dashboard.RefreshPowerState();
    }

    private async void ToggleGpuOverridesAsync()
    {
        bool want = !_settings.GpuOverridesEnabled;
        _miGpuOverrides.Enabled = false;
        try
        {
            var r = await Task.Run(() => want
                ? GpuPreference.ApplyAll(_settings.GpuOverrides)
                : GpuPreference.ClearAll(_settings.GpuOverrides));
            if (r.Ok)
            {
                _settings.GpuOverridesEnabled = want;
                _settings.Save();
            }
            else
            {
                Balloon("Per-app GPU override", r.Message, ToolTipIcon.Warning);
            }
        }
        finally
        {
            _miGpuOverrides.Enabled = true;
        }

        SyncMenuFromSettings();
        _dashboard.RefreshPowerState();
    }

    private void ToggleAutoStart()
    {
        if (_settings.AutoStart)
        {
            var (ok, msg) = AutoStart.Unregister();
            Balloon("Auto-start", msg, ok ? ToolTipIcon.Info : ToolTipIcon.Error);
        }
        else
        {
            var (ok, msg) = AutoStart.Register(_settings.AutoStartSilent);
            Balloon("Auto-start", msg, ok ? ToolTipIcon.Info : ToolTipIcon.Error);
        }
        SyncMenuFromSettings();
        _settings.Save();
    }

    private void ToggleAutoStartSilent()
    {
        if (!_settings.AutoStart) return;
        bool silent = !_settings.AutoStartSilent;
        var (ok, msg) = AutoStart.Register(silent);
        Balloon("Auto-start", msg, ok ? ToolTipIcon.Info : ToolTipIcon.Error);
        SyncMenuFromSettings();
        _settings.Save();
    }

    // --- live update --------------------------------------------------------

    private void OnSnapshot(FanSnapshot s)
    {
        if (_exiting) return;
        try
        {
            _marshal.BeginInvoke(() =>
            {
                if (_exiting) return;
                UpdateIcon(s);
                _icon.Text = Truncate(
                    "CPU " + Fmt(s.CpuC, "°C") + " · GPU " + Fmt(s.GpuC, "°C") + " · " +
                    Fmt(s.Fan0Rpm, "") + "/" + Fmt(s.Fan1Rpm, "") + " rpm · " + s.ModeText);

                _miMax.Checked = s.FansPinned;
                _miRelease.Enabled = s.CustomEngaged;
                _miModeQuiet.Checked = !s.CustomEngaged && s.SmartFanMode == LenovoWmi.ModeQuiet;
                _miModeBalanced.Checked = !s.CustomEngaged && s.SmartFanMode == LenovoWmi.ModeBalanced;
                _miModePerformance.Checked = !s.CustomEngaged && s.SmartFanMode == LenovoWmi.ModePerformance;

                if (_dashboard.Visible) _dashboard.Apply(s);
            });
        }
        catch (ObjectDisposedException) { /* tray torn down mid-poll */ }
        catch (InvalidOperationException) { /* handle gone during shutdown */ }
    }

    private void OnNotice(string message, bool bad)
    {
        if (_exiting) return;
        try
        {
            _marshal.BeginInvoke(() => Balloon("Legion Fan Tray", message, bad ? ToolTipIcon.Warning : ToolTipIcon.Info));
        }
        catch (ObjectDisposedException) { }
        catch (InvalidOperationException) { }
    }

    /// <summary>
    /// Renders the CPU temperature straight into the tray icon. The previous
    /// HICON must be destroyed every update or the process leaks GDI handles.
    /// </summary>
    private void UpdateIcon(FanSnapshot s)
    {
        if (!_settings.ShowTempInTrayIcon)
        {
            if (_lastIconText != "<default>")
            {
                _lastIconText = "<default>";
                SwapIcon(SystemIcons.Application, IntPtr.Zero);
            }
            return;
        }

        string text = s.CpuC?.ToString() ?? "--";

        // The icon sits on the taskbar, not in our window, so it follows the
        // SYSTEM theme rather than the app's. Part of the cache key, so a theme
        // change forces a redraw.
        bool lightTaskbar = IsLightTaskbar();

        // Render at twice the tray's icon size so the downscale stays crisp,
        // and never below 32 so two digits have room to be legible. Part of the
        // cache key too, since a DPI change resizes the tray icon.
        int size = Math.Max(32, SystemInformation.SmallIconSize.Width * 2);

        string key = text + (s.FansPinned ? "!" : "") + (lightTaskbar ? "L" : "D") + size;
        if (key == _lastIconText) return;
        _lastIconText = key;

        using var bmp = new Bitmap(size, size);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
            g.Clear(Color.Transparent);

            Color fg = lightTaskbar
                ? s.FansPinned ? Color.FromArgb(190, 60, 40)
                    : s.CpuC >= 85 ? Color.FromArgb(200, 45, 40)
                    : s.CpuC >= 65 ? Color.FromArgb(180, 115, 20)
                    : Color.FromArgb(32, 32, 36)
                : s.FansPinned ? Color.FromArgb(255, 120, 90)
                    : s.CpuC >= 85 ? Color.FromArgb(255, 110, 100)
                    : s.CpuC >= 65 ? Color.FromArgb(255, 200, 90)
                    : Color.White;

            using var brush = new SolidBrush(fg);
            DrawFilling(g, text, size, brush);
        }

        IntPtr h = bmp.GetHicon();
        SwapIcon(Icon.FromHandle(h), h);
    }

    /// <summary>
    /// Draws the reading scaled to fill the icon.
    ///
    /// Sizing by font size does not work here: MeasureString returns the line
    /// box, which includes ascent and descent the digits never occupy, so the
    /// glyphs end up well short of the edges. At 16x16 in the tray that wasted
    /// space is the difference between readable and not. Measuring the glyph
    /// outline instead and scaling it to the box uses every pixel available.
    /// </summary>
    private static void DrawFilling(Graphics g, string text, int size, Brush brush)
    {
        float pad = Math.Max(1f, size / 16f);
        float box = size - pad * 2;

        using var format = new StringFormat(StringFormat.GenericTypographic)
        {
            FormatFlags = StringFormatFlags.NoWrap | StringFormatFlags.NoClip,
        };
        using var path = new GraphicsPath();
        path.AddString(text, IconFamily, (int)FontStyle.Bold, 100f, new PointF(0, 0), format);

        var b = path.GetBounds();
        if (b.Width <= 0 || b.Height <= 0) return;

        float scale = Math.Min(box / b.Width, box / b.Height);
        using var m = new Matrix();
        m.Translate(-b.X, -b.Y);
        m.Scale(scale, scale, MatrixOrder.Append);
        m.Translate((size - b.Width * scale) / 2f, (size - b.Height * scale) / 2f, MatrixOrder.Append);
        path.Transform(m);

        g.FillPath(brush, path);
    }

    private static FontFamily LoadIconFamily()
    {
        try { return new FontFamily("Segoe UI"); }
        catch { return FontFamily.GenericSansSerif; }
    }

    /// <summary>
    /// SystemUsesLightTheme is the taskbar/tray theme, distinct from
    /// AppsUseLightTheme. Missing key (older builds) means the dark taskbar.
    /// </summary>
    private static bool IsLightTaskbar()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("SystemUsesLightTheme") is int v && v != 0;
        }
        catch { return false; }
    }

    private void SwapIcon(Icon icon, IntPtr newHandle)
    {
        var old = _iconHandle;
        _icon.Icon = icon;
        _iconHandle = newHandle;
        if (old != IntPtr.Zero) DestroyIcon(old);
    }

    // --- actions ------------------------------------------------------------

    private async Task WriteAsync(Func<Task<WriteResult>> action)
    {
        _miMax.Enabled = false;
        _miRelease.Enabled = false;
        try
        {
            var r = await action();
            Balloon("Legion Fan Tray", r.Message, r.Ok ? ToolTipIcon.Info : ToolTipIcon.Warning);
        }
        catch (Exception ex)
        {
            Balloon("Legion Fan Tray", ex.Message, ToolTipIcon.Error);
        }
        finally
        {
            _miMax.Enabled = true;
            _miRelease.Enabled = true;
        }
    }

    private void ShowDashboard()
    {
        if (!_dashboard.Visible)
        {
            var wa = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1280, 800);
            _dashboard.Location = new Point(
                wa.Right - _dashboard.Width - 16,
                wa.Bottom - _dashboard.Height - 16);
            _dashboard.Show();
        }
        _dashboard.Activate();
        _controller.DashboardVisible = true;
        _dashboard.VisibleChanged -= OnDashboardVisibleChanged;
        _dashboard.VisibleChanged += OnDashboardVisibleChanged;
        if (_controller.Latest != null) _dashboard.Apply(_controller.Latest);

        // The cap lives per power scheme, so re-read it each time the window
        // opens rather than trusting what we last displayed.
        _dashboard.RefreshCap();
    }

    private void OnDashboardVisibleChanged(object? sender, EventArgs e) =>
        _controller.DashboardVisible = _dashboard.Visible;

    private void ExitApp()
    {
        _exiting = true;
        _icon.Visible = false;
        ExitThread();
    }

    // --- helpers ------------------------------------------------------------

    private void Balloon(string title, string text, ToolTipIcon icon)
    {
        try
        {
            _icon.BalloonTipTitle = title;
            _icon.BalloonTipText = Truncate(text, 250);
            _icon.BalloonTipIcon = icon;
            _icon.ShowBalloonTip(5000);
        }
        catch { /* balloons are cosmetic */ }
    }

    private static string Fmt(int? v, string unit) => v.HasValue ? v.Value + unit : "n/a";

    // NotifyIcon.Text throws above 63 characters.
    private static string Truncate(string s, int max = 63) =>
        s.Length <= max ? s : s[..(max - 1)] + "…";

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _controller.SnapshotUpdated -= OnSnapshot;
            _controller.Notice -= OnNotice;
            _icon.Visible = false;
            _icon.Dispose();
            if (_iconHandle != IntPtr.Zero) DestroyIcon(_iconHandle);
            _dashboard.Dispose();
            _marshal.Dispose();
        }
        base.Dispose(disposing);
    }
}

