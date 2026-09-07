using System.Reflection;

namespace LegionFanTray;

/// <summary>
/// The version stamped into the assembly at build time from appVersion.txt.
///
/// Read back off the assembly rather than kept as a constant here, so there is
/// nothing in the source to forget to bump alongside the file.
/// </summary>
internal static class AppVersion
{
    /// <summary>
    /// What the informational version says in full. A CI build appends the
    /// short commit SHA, so this can read "0.1.0+a1b2c3d" and a local build
    /// just "0.1.0".
    ///
    /// GetCustomAttribute is used rather than Assembly.Location, which comes
    /// back empty under PublishSingleFile -- the usual trap in single-file apps.
    /// </summary>
    public static string Full { get; } = Read();

    /// <summary>
    /// Just the number, cut at the "+". This is the one to put in front of a
    /// user; the commit SHA only matters when someone is reporting a bug.
    /// </summary>
    public static string Short { get; } = Full.Split('+')[0];

    /// <summary>"Legion Fan Tray 0.1.0" -- window titles and the menu header.</summary>
    public static string Title { get; } = "Legion Fan Tray " + Short;

    private static string Read()
    {
        try
        {
            var asm = Assembly.GetExecutingAssembly();
            var info = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            if (!string.IsNullOrWhiteSpace(info)) return info.Trim();

            return asm.GetName().Version?.ToString(3) ?? "unknown";
        }
        catch
        {
            // A missing version is not worth taking the app down for.
            return "unknown";
        }
    }
}
