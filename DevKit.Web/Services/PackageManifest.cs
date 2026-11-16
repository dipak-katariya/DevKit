using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;

namespace DevKit.Web.Services;

/// <summary>
/// package.txt, which devkit-deploy.bat has the package's own new DevKit.Web.exe write last (<see cref="Option"/>): how
/// many files the package has, their size, and the SHA-256 of each. "Run Latest DevKit.bat" compares the count and size
/// to pick a whole package fast; the switch (<see cref="DevKitSwitch"/>) checks every hash before it runs anything, so a
/// package copied in part, damaged or changed after it was made is refused. It is no lock against someone who may write
/// to the folder, as they could write package.txt too: the folder's permissions are (<see cref="FolderAccess"/>).
/// </summary>
internal static class PackageManifest
{
    /// <summary>DevKit.Web.exe --write-manifest &lt;package folder&gt; writes the folder's package.txt.</summary>
    public const string Option = "--write-manifest";

    private const string FilesKey = "files=";
    private const string BytesKey = "bytes=";
    private const string HashKey = "sha256=";
    private const string AppFolder = "app";
    private const string RuntimeConfigFile = "DevKit.Web.runtimeconfig.json";
    private const string PatchedRuntimeKey = "DevKitPatchedRuntime";

    // Only a self-contained package has the runtime's own files beside the program.
    private const string SelfContainedMarker = "coreclr.dll";

    public static bool IsRequested(string[] args) => args.Contains(Option, StringComparer.OrdinalIgnoreCase);

    /// <summary>Writes package.txt into the folder after <see cref="Option"/>; the exit code, 0 when it was written.</summary>
    public static int Run(string[] args)
    {
        using var loggers = LoggerFactory.Create(logging => logging.AddSimpleConsole(console => console.SingleLine = true));
        var log = loggers.CreateLogger(nameof(PackageManifest));
        var at = Array.FindIndex(args, arg => arg.Equals(Option, StringComparison.OrdinalIgnoreCase));
        if (at + 1 >= args.Length || !Directory.Exists(args[at + 1]))
        {
            log.LogError("{Option} needs the package folder to write package.txt into", Option);
            return 1;
        }

        var folder = Path.GetFullPath(args[at + 1]);
        try
        {
            if (RuntimeProblem(folder) is { Length: > 0 } outdated)
            {
                log.LogError("{File} was not written in {Folder}: {Problem}", DevKitSwitch.ManifestFileName, folder, outdated);
                return 1;
            }

            Write(folder);
            log.LogInformation("Wrote {File} in {Folder}", DevKitSwitch.ManifestFileName, folder);
            return 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            log.LogError(ex, "{File} could not be written in {Folder}", DevKitSwitch.ManifestFileName, folder);
            return 1;
        }
    }

    /// <summary>
    /// What keeps a self-contained package's own .NET from being one with Microsoft's latest security fixes: a framework
    /// it carries older than <see cref="PatchedRuntime"/>. Empty when there is none, and for a folder that carries no .NET
    /// of its own. Windows Update never patches a runtime inside a package, so an old one would stay unpatched.
    /// </summary>
    internal static string RuntimeProblem(string folder)
    {
        var app = Path.Combine(folder, AppFolder);
        var config = Path.Combine(app, RuntimeConfigFile);
        if (!File.Exists(Path.Combine(app, SelfContainedMarker)) || !File.Exists(config))
        {
            return "";
        }

        using var document = JsonDocument.Parse(File.ReadAllText(config));
        if (!document.RootElement.TryGetProperty("runtimeOptions", out var options)
            || !options.TryGetProperty("includedFrameworks", out var frameworks)
            || frameworks.ValueKind != JsonValueKind.Array)
        {
            return $"{RuntimeConfigFile} does not say which .NET the package carries";
        }

        foreach (var framework in frameworks.EnumerateArray())
        {
            var name = framework.TryGetProperty("name", out var named) ? named.GetString() : null;
            var version = framework.TryGetProperty("version", out var versioned) ? versioned.GetString() : null;
            if (!Version.TryParse(version, out var carried) || carried < PatchedRuntime)
            {
                return $"the package carries {name} {version}, older than {PatchedRuntime}, the .NET release with Microsoft's latest security fixes. " +
                       "Make the package again with the DevKitPatchedRuntime that DevKit.Web.csproj names";
            }
        }

        return "";
    }

    /// <summary>The .NET release a package must carry at least: DevKitPatchedRuntime in DevKit.Web.csproj.</summary>
    internal static Version PatchedRuntime { get; } = Version.Parse(
        typeof(PackageManifest).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .Single(attribute => attribute.Key == PatchedRuntimeKey).Value!);

    /// <summary>Writes <paramref name="folder"/>'s package.txt from the files it has now.</summary>
    public static void Write(string folder)
    {
        var files = PackageFiles(folder);
        var lines = new List<string>
        {
            FilesKey + files.Count.ToString(CultureInfo.InvariantCulture),
            BytesKey + files.Sum(file => file.Value.Length).ToString(CultureInfo.InvariantCulture)
        };
        lines.AddRange(files.Select(file => $"{HashKey}{Hash(file.Value)} {file.Key}"));
        File.WriteAllLines(Path.Combine(folder, DevKitSwitch.ManifestFileName), lines);
    }

    /// <summary>
    /// What keeps <paramref name="folder"/>'s files from being the ones its package.txt lists; empty when nothing does,
    /// and when it has no package.txt, as for a package made before there was one, which only a person names.
    /// </summary>
    public static string Problem(string folder)
    {
        var manifest = Path.Combine(folder, DevKitSwitch.ManifestFileName);
        if (!File.Exists(manifest))
        {
            return "";
        }

        var listed = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in File.ReadLines(manifest).Where(line => line.StartsWith(HashKey, StringComparison.Ordinal)))
        {
            var space = line.IndexOf(' ', HashKey.Length);
            if (space < 0)
            {
                return $"{DevKitSwitch.ManifestFileName} has a line that is not a file's hash";
            }

            listed[line[(space + 1)..]] = line[HashKey.Length..space];
        }

        if (listed.Count == 0)
        {
            return $"{DevKitSwitch.ManifestFileName} lists no hashes: make the package again with devkit-deploy.bat";
        }

        var files = PackageFiles(folder);
        if (files.Keys.FirstOrDefault(name => !listed.ContainsKey(name)) is { } extra)
        {
            return $"it has {extra}, which {DevKitSwitch.ManifestFileName} does not list";
        }

        if (listed.Keys.FirstOrDefault(name => !files.ContainsKey(name)) is { } missing)
        {
            return $"{missing} is missing";
        }

        return listed.FirstOrDefault(entry => !entry.Value.Equals(Hash(files[entry.Key]), StringComparison.OrdinalIgnoreCase)).Key is { } changed
            ? $"{changed} is not the file the package was made with"
            : "";
    }

    /// <summary>The package's files by their path in it, but for the two the package itself writes, as the scripts count them.</summary>
    private static Dictionary<string, FileInfo> PackageFiles(string folder) =>
        new DirectoryInfo(folder)
            .EnumerateFiles("*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = 0 })
            .Where(file => !file.Name.Equals(DevKitSwitch.ManifestFileName, StringComparison.OrdinalIgnoreCase)
                           && !file.Name.Equals(DevKitSwitch.FailedFileName, StringComparison.OrdinalIgnoreCase))
            .ToDictionary(file => Path.GetRelativePath(folder, file.FullName), StringComparer.OrdinalIgnoreCase);

    private static string Hash(FileInfo file)
    {
        using var stream = file.OpenRead();
        return Convert.ToHexString(SHA256.HashData(stream));
    }
}
