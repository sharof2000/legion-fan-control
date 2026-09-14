using System.Diagnostics;

namespace LegionFanTray;

/// <summary>
/// Opens a vendor control panel, or says why it could not. Shared by the
/// NVIDIA and AMD rows, which do the same thing against different paths.
/// </summary>
internal static class VendorApp
{
    /// <summary>First candidate that exists under either Program Files root.</summary>
    public static WriteResult LaunchFirst(string[] relativePaths, string missing)
    {
        foreach (string root in new[]
                 {
                     Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                     Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                 })
        {
            if (string.IsNullOrEmpty(root)) continue;
            foreach (string rel in relativePaths)
            {
                string full = Path.Combine(root, rel);
                if (File.Exists(full)) return Launch(full);
            }
        }
        return WriteResult.Failure(missing);
    }

    public static WriteResult Launch(string target)
    {
        try
        {
            // UseShellExecute is required for both the ms-settings: protocol and
            // for launching a GUI app without inheriting our elevated console.
            Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
            return WriteResult.Success("Opened " + Path.GetFileNameWithoutExtension(target) + ".");
        }
        catch (Exception ex)
        {
            return WriteResult.Failure("Could not open " + target + ": " + ex.Message);
        }
    }
}

/// <summary>
/// The one thing on this tab the app will not automate.
///
/// Battery Boost, Max Frame Rate and Whisper Mode live in NVIDIA's binary
/// driver profile database (nvdrsdb0.bin), reachable only through the
/// undocumented NVAPI DRS entry points -- hardcoded function and setting IDs
/// that can corrupt the profile DB and that move between driver releases.
/// For a setting flipped once, that is a bad trade. So: detect the symptom,
/// say what it is, and open the right page.
///
/// Measured on this machine: with the cap in place the dGPU is held to 30 FPS
/// on battery, which is why the integrated Radeon is the faster path for the
/// Unity Editor on DC (206 FPS at 1080p vs 30).
/// </summary>
internal static class NvidiaHints
{
    public const string CapWarning =
        "On battery: the dGPU is likely capped (Battery Boost defaults to 30 FPS).";

    public const string CapIdle =
        "On AC: no battery FPS cap applies.";

    public const string Steps =
        "NVIDIA app -> Graphics -> Battery Boost.\r\n" +
        "Its default is a 30 FPS cap on battery power.\r\n" +
        "Check Max Frame Rate and Whisper Mode on the same page.\r\n\r\n" +
        "This app cannot read or write these: they live in NVIDIA's binary\r\n" +
        "driver profile database, which has no supported API.";

    public static string Describe(bool onAc) => onAc ? CapIdle : CapWarning;

    private static readonly string[] Candidates =
    {
        @"NVIDIA Corporation\NVIDIA app\CEF\NVIDIA app.exe",
        @"NVIDIA Corporation\NVIDIA App\CEF\NVIDIA app.exe",
        @"NVIDIA Corporation\NVIDIA GeForce Experience\NVIDIA GeForce Experience.exe",
        @"NVIDIA Corporation\Control Panel Client\nvcplui.exe",
    };

    /// <summary>Launches the first NVIDIA front-end that is actually installed.</summary>
    public static WriteResult LaunchNvidiaApp()
    {
        var r = VendorApp.LaunchFirst(Candidates,
            "No NVIDIA app found under Program Files. Open it from the Start menu.");
        return r.Ok ? WriteResult.Success(r.Message + " Go to Graphics -> Battery Boost.") : r;
    }

    /// <summary>Windows' own per-app graphics page, the UI behind GpuPreference.</summary>
    public static WriteResult OpenWindowsGraphicsSettings() =>
        VendorApp.Launch("ms-settings:display-advancedgraphics");
}

