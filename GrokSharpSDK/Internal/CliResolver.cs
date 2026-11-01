using System.Text.Json;
using ManagedCode.GrokSharpSDK.Models;

namespace ManagedCode.GrokSharpSDK.Internal;

internal static class CliResolver
{
    internal const string PathVariable = "PATH";
    private const string Grok = "grok";
    private const string Node = "node";
    private const string NodeModules = "node_modules";
    private const string NpmBin = ".bin";
    private const string Scope = "@xai-official";
    private const string PackageName = "@xai-official/grok";
    private const string PackageManifest = "package.json";
    private const string MissingMessage = "Grok Build CLI was not found. Install @xai-official/grok or configure an absolute ExecutablePath.";
    private const string InvalidShimMessage = "The selected wrapper is not adjacent to the official @xai-official/grok npm package.";

    internal static CliLaunchCommand Resolve(string? configured, string? path)
    {
        var selected = configured ?? Grok;
        var file = Path.IsPathFullyQualified(selected) || selected.Contains(Path.DirectorySeparatorChar)
            ? Path.GetFullPath(selected)
            : FindOnPath(selected, path);
        if (!File.Exists(file))
        {
            throw new FileNotFoundException(MissingMessage, file);
        }
        var extension = Path.GetExtension(file);
        if (extension.Equals(".cmd", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".bat", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".ps1", StringComparison.OrdinalIgnoreCase))
        {
            var directory = Path.GetDirectoryName(file)!;
            var package = Path.GetFileName(directory) == NpmBin
                ? Path.Combine(Directory.GetParent(directory)!.FullName, Scope, Grok)
                : Path.Combine(directory, NodeModules, Scope, Grok);
            var manifest = Path.Combine(package, PackageManifest);
            if (!File.Exists(manifest))
            {
                throw new InvalidDataException(InvalidShimMessage);
            }
            using var document = JsonDocument.Parse(File.ReadAllText(manifest));
            var root = document.RootElement;
            if (root.GetProperty("name").GetString() != PackageName)
            {
                throw new InvalidDataException(InvalidShimMessage);
            }
            var bin = root.GetProperty("bin");
            var entry = bin.ValueKind == JsonValueKind.String ? bin.GetString()! : bin.GetProperty(Grok).GetString()!;
            var entryPath = Path.GetFullPath(Path.Combine(package, entry));
            var relative = Path.GetRelativePath(package, entryPath);
            if (relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathFullyQualified(relative) || !File.Exists(entryPath))
            {
                throw new InvalidDataException(InvalidShimMessage);
            }
            return new CliLaunchCommand(FindOnPath(OperatingSystem.IsWindows() ? "node.exe" : Node, path), [entryPath]);
        }
        return extension.Equals(".js", StringComparison.OrdinalIgnoreCase)
            ? new CliLaunchCommand(FindOnPath(Node, path), [file])
            : new CliLaunchCommand(file, []);
    }

    private static string FindOnPath(string name, string? path)
    {
        string[] names = OperatingSystem.IsWindows() && !Path.HasExtension(name)
            ? [name + ".exe", name + ".cmd", name] : [name];
        foreach (var directory in (path ?? string.Empty).Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (var candidate in names)
            {
                var file = Path.GetFullPath(Path.Combine(directory.Trim('"'), candidate));
                if (File.Exists(file))
                {
                    return file;
                }
            }
        }
        throw new FileNotFoundException(MissingMessage, name);
    }
}
