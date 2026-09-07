using System.Text;

namespace LegionFanTray;

/// <summary>
/// Logon auto-start.
///
/// This does NOT use HKCU\...\Run. Windows silently skips elevated apps listed
/// there -- no error, no prompt, the app simply never starts. Task Scheduler is
/// the only mechanism that can launch an elevated app at logon at all, so the
/// task exists either way and the privilege level is the switch:
///
///   RunLevel LUA     -> task starts the exe, the manifest raises UAC, user approves.
///   RunLevel HIGHEST -> silent elevated start, no prompt.
/// </summary>
internal static class AutoStart
{
    public const string TaskName = "LegionFanTray";

    public static string ExePath =>
        Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "LegionFanTray.exe");

    public static bool IsRegistered() => Run("/Query /TN \"" + TaskName + "\"").ExitCode == 0;

    /// <summary>True when the task is registered to run elevated without a prompt.</summary>
    public static bool IsSilent()
    {
        var r = Run("/Query /TN \"" + TaskName + "\" /XML");
        if (r.ExitCode != 0) return false;
        return r.Output.Contains("HighestAvailable", StringComparison.OrdinalIgnoreCase);
    }

    public static (bool Ok, string Message) Register(bool silent)
    {
        var user = Environment.UserDomainName + "\\" + Environment.UserName;
        var sb = new StringBuilder();
        sb.Append("/Create /F /TN \"").Append(TaskName).Append('"');
        sb.Append(" /SC ONLOGON");
        sb.Append(" /RU \"").Append(user).Append('"');
        if (silent) sb.Append(" /RL HIGHEST");
        sb.Append(" /TR \"\\\"").Append(ExePath).Append("\\\"\"");

        var r = Run(sb.ToString());
        if (r.ExitCode == 0)
        {
            return (true, silent
                ? "Registered to start at logon with no UAC prompt."
                : "Registered to start at logon. You will get a UAC prompt once per boot.");
        }
        return (false, "schtasks failed (" + r.ExitCode + "): " + FirstLine(r.Output + r.Error));
    }

    public static (bool Ok, string Message) Unregister()
    {
        var r = Run("/Delete /F /TN \"" + TaskName + "\"");
        if (r.ExitCode == 0) return (true, "Auto-start removed.");
        return (false, "schtasks failed (" + r.ExitCode + "): " + FirstLine(r.Output + r.Error));
    }

    private static string FirstLine(string s)
    {
        var line = s.Split('\n').FirstOrDefault(l => !string.IsNullOrWhiteSpace(l));
        return line?.Trim() ?? "no output";
    }

    private static (int ExitCode, string Output, string Error) Run(string args) =>
        Shell.Run("schtasks.exe", args);
}
