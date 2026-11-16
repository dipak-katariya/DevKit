using System.Globalization;
using System.Text;

namespace DevKit.Web.Services;

/// <summary>
/// Which package runs DevKit on a port, and the run of the switch that made it so (<see cref="DevKitSwitch"/>). It is
/// kept once per Windows account and port, beside devkit.json, whichever Release folder the packages are in: every
/// package's keep-alive script (devkit-run.bat) reads it and stays stopped once it names another package or run, and the
/// Startup file starts the package it names at sign-in. live.txt in the Release folder names the same package and run,
/// for the scripts of packages made before this state existed, which read only that.
/// </summary>
internal sealed record LivePackage(string PackageFolder, string? Run)
{
    private const string PointerFileName = "live.txt";

    /// <summary>Where the state for <paramref name="port"/> is kept; tools\set-hosted-env.bat names the same file.</summary>
    public static string StatePath(int port) =>
        Path.Combine(DevKitMachineConfig.Folder, "live-" + port.ToString(CultureInfo.InvariantCulture) + ".txt");

    /// <summary>
    /// The state for <paramref name="port"/>; when there is none yet, what live.txt in <paramref name="release"/> names,
    /// as a switch from before the state existed wrote it; null when neither names a package.
    /// </summary>
    public static LivePackage? Read(int port, string release)
    {
        var state = ReadLines(StatePath(port));
        if (state is [{ Length: > 0 } folder, ..])
        {
            return new LivePackage(folder, RunIn(state));
        }

        var pointer = ReadLines(Path.Combine(release, PointerFileName));
        return pointer is [{ Length: > 0 } name, ..] && DevKitSwitch.IsPackageName(name)
            ? new LivePackage(Path.Combine(release, name), RunIn(pointer))
            : null;
    }

    /// <summary>Makes this the package that runs on <paramref name="port"/>, in the state and in live.txt in <paramref name="release"/>.</summary>
    public void Write(int port, string release)
    {
        WriteLines(StatePath(port), PackageFolder);
        WriteLines(Path.Combine(release, PointerFileName), Path.GetFileName(PackageFolder));
    }

    /// <summary>Forgets which package runs on <paramref name="port"/>, as before any switch: every package's script keeps its DevKit running again.</summary>
    public static void Clear(int port, string release)
    {
        File.Delete(StatePath(port));
        File.Delete(Path.Combine(release, PointerFileName));
    }

    private void WriteLines(string path, string first)
    {
        // Replaced whole, so a script never reads half of it.
        var written = path + ".tmp";
        File.WriteAllText(written, first + Environment.NewLine + Run + Environment.NewLine, ScriptText);
        File.Move(written, path, overwrite: true);
    }

    private static string? RunIn(string[] lines) => lines.Length > 1 ? PortHandover.RunFrom(lines[1].Trim()) : null;

    private static string[]? ReadLines(string path) => File.Exists(path) ? File.ReadAllLines(path, ScriptText) : null;

    // What cmd reads with set /p: the console's code page, so a folder name outside ASCII reads back as itself.
    private static Encoding ScriptText
    {
        get
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            return Encoding.GetEncoding(CultureInfo.CurrentCulture.TextInfo.OEMCodePage);
        }
    }
}
