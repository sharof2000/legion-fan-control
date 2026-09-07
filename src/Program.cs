using System.Security.Principal;
using Microsoft.Win32;

namespace LegionFanTray;

internal static class Program
{
    private static Mutex? _mutex;
    private static FanController? _controller;

    [STAThread]
    private static void Main()
    {
        // Two copies both writing fan state would fight over the firmware.
        _mutex = new Mutex(true, @"Global\LegionFanTray.SingleInstance", out bool createdNew);
        if (!createdNew) return;

        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        if (!IsElevated())
        {
            // The manifest requests admin, so this only happens if it was
            // stripped. Reads return "Access denied" without it, not just writes.
            MessageBox.Show(
                "Legion Fan Tray must run elevated.\n\n" +
                "The Lenovo WMI classes deny reads as well as writes to non-admin processes.",
                "Legion Fan Tray", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        AppPaths.EnsureDir();

        var settings = Settings.Load();
        var wmi = new LenovoWmi();
        var controller = new FanController(wmi, settings);
        _controller = controller;

        // Revert layers 1-4. Layer 5 (the lock file) covers what these cannot:
        // Task Manager "End task" and hard power loss, where nothing runs.
        Application.ApplicationExit += (_, _) => controller.EmergencyRelease();
        AppDomain.CurrentDomain.ProcessExit += (_, _) => controller.EmergencyRelease();
        SystemEvents.SessionEnding += (_, _) => controller.EmergencyRelease();
        Application.ThreadException += (_, e) => Fatal(e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Fatal(e.ExceptionObject as Exception);

        controller.RecoverFromDirtyLock();
        controller.Start();

        try
        {
            using var tray = new TrayApp(controller, settings);
            Application.Run(tray);
        }
        finally
        {
            controller.EmergencyRelease();
            controller.Dispose();
            wmi.Dispose();
            _mutex.ReleaseMutex();
        }
    }

    private static bool IsElevated()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch { return false; }
    }

    /// <summary>
    /// Release the fans first, log second. A crash that leaves the fans pinned
    /// at 4300 rpm until reboot is the worst outcome here.
    /// </summary>
    private static void Fatal(Exception? ex)
    {
        try { _controller?.EmergencyRelease(); } catch { }
        try
        {
            AppPaths.EnsureDir();
            File.AppendAllText(AppPaths.CrashLog,
                DateTime.Now.ToString("s") + "  " + (ex?.ToString() ?? "unknown error") + Environment.NewLine);
        }
        catch { }

        try
        {
            MessageBox.Show(
                "Legion Fan Tray hit an unexpected error and released the fans.\n\n" +
                (ex?.Message ?? "unknown error") + "\n\nDetails: " + AppPaths.CrashLog,
                "Legion Fan Tray", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        catch { }

        Environment.Exit(1);
    }
}
