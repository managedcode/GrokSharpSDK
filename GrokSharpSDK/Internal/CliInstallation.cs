using System.Runtime.CompilerServices;
using ManagedCode.GrokSharpSDK.Configuration;
using ManagedCode.GrokSharpSDK.Models;

namespace ManagedCode.GrokSharpSDK.Internal;

internal static class CliInstallation
{
    private const string Package = "@xai-official/grok";
    internal static async IAsyncEnumerable<CliInstallationUpdate> InstallAsync(CliInstallationOptions options,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(options.InstallTimeout, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.MaximumOutputCharacters);
        cancellationToken.ThrowIfCancellationRequested();
        if (!Enum.IsDefined(options.PackageManager) ||
            !Path.IsPathFullyQualified(options.PackageManagerExecutablePath) ||
            !File.Exists(options.PackageManagerExecutablePath) ||
            (options.PackageManager == CliPackageManager.Npm &&
                (options.NpmCliScriptPath is null || !Path.IsPathFullyQualified(options.NpmCliScriptPath) || !File.Exists(options.NpmCliScriptPath))) ||
            (options.PackageManager == CliPackageManager.Bun && options.NpmCliScriptPath is not null))
        {
            throw new ArgumentException("An explicit native package-manager executable and matching npm script are required.", nameof(options));
        }
        var parent = options.InstallationDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ManagedCode", "GrokSharpSDK", "cli");
        if (!Path.IsPathFullyQualified(parent))
        {
            throw new ArgumentException("The installation parent must be absolute.", nameof(options));
        }
        // Each attempt owns a separate immutable generation; concurrent attempts never share files.
        var root = Path.Combine(parent, GrokCliCompatibility.TargetVersion, Guid.CreateVersion7().ToString("N"));
        var environment = CreateEnvironment(options.EnvironmentVariables, root);
        var completed = false;
        var cleanupConfirmed = true;
        try
        {
            var versionedPackage = string.Concat(Package, "@", GrokCliCompatibility.TargetVersion);
            var arguments = options.PackageManager == CliPackageManager.Npm
                ? new[] { "install", "--prefix", root, "--no-save", "--package-lock=false", versionedPackage }
                : new[] { "install", "--cwd", root, "--no-save", "--trust", versionedPackage };
            var launch = options.PackageManager == CliPackageManager.Npm
                ? new CliLaunchCommand(options.PackageManagerExecutablePath, options.NpmCliScriptPath!)
                : new CliLaunchCommand(options.PackageManagerExecutablePath);
            var processOptions = new GrokOptions
            {
                WorkingDirectory = root,
                LaunchCommand = launch,
                EnvironmentVariables = environment,
                InheritEnvironmentVariables = false,
                ProcessTerminationTimeout = options.ProcessTerminationTimeout,
            };
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(options.InstallTimeout);
            var process = new CliProcess(processOptions, arguments);
            try
            {
                yield return new(CliInstallationStage.PackageManagerStarted);
                var stdout = 0;
                var stderr = 0;
                await foreach (var output in process.ReadAsync(deadline.Token).ConfigureAwait(false))
                {
                    var count = checked(output.Line.Length + Environment.NewLine.Length);
                    if ((long)stdout + stderr + count > options.MaximumOutputCharacters)
                    {
                        throw new InvalidDataException("Package-manager output exceeded the configured budget.");
                    }
                    if (output.StandardError) { stderr += count; } else { stdout += count; }
                    yield return new(CliInstallationStage.OutputObserved, stdout, stderr);
                }
                if (await process.WaitForExitAsync(deadline.Token).ConfigureAwait(false) != 0)
                {
                    throw new InvalidOperationException("The official Grok package installation failed.");
                }
                yield return new(CliInstallationStage.Verifying, stdout, stderr);
                var executable = Path.Combine(environment["GROK_HOME"], "bin", OperatingSystem.IsWindows() ? "grok.exe" : "grok");
                var native = new CliLaunchCommand(executable);
                var version = CliMetadataProbe.ReadVersion(processOptions with { LaunchCommand = native },
                    options.InstallTimeout, options.MaximumOutputCharacters);
                if (version != GrokCliCompatibility.TargetVersion)
                {
                    throw new InvalidDataException("Installed Grok version differs from the pinned SDK target.");
                }
                await process.DisposeAsync().ConfigureAwait(false);
                completed = true;
                yield return new(CliInstallationStage.Installed, stdout, stderr,
                    new(version, GrokCliCompatibility.TargetVersion, root, native));
            }
            finally
            {
                cleanupConfirmed = false;
                await process.DisposeAsync().ConfigureAwait(false);
                cleanupConfirmed = true;
            }
        }
        finally
        {
            if (!completed && cleanupConfirmed && Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private static Dictionary<string, string> CreateEnvironment(IReadOnlyDictionary<string, string> configured, string root)
    {
        string[] allowed = ["PATH", "SYSTEMROOT", "LANG", "LC_ALL", "SSL_CERT_FILE", "SSL_CERT_DIR"];
        var result = new Dictionary<string, string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        foreach (var (name, value) in configured)
        {
            var canonical = allowed.FirstOrDefault(item => item.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (canonical is null || string.IsNullOrWhiteSpace(value))
            {
                throw new ArgumentException("The installation environment must contain only safe package-manager settings.", nameof(configured));
            }
            result[canonical] = value;
        }
        if (!result.ContainsKey("PATH") || (OperatingSystem.IsWindows() && !result.ContainsKey("SYSTEMROOT")))
        {
            throw new ArgumentException("Installation requires PATH and Windows SYSTEMROOT.", nameof(configured));
        }
        result["HOME"] = Path.Combine(root, "home");
        result["USERPROFILE"] = result["HOME"];
        result["GROK_HOME"] = Path.Combine(root, "grok");
        result["XDG_CONFIG_HOME"] = Path.Combine(root, "config");
        result["XDG_CACHE_HOME"] = Path.Combine(root, "cache");
        result["APPDATA"] = result["XDG_CONFIG_HOME"];
        result["LOCALAPPDATA"] = result["XDG_CACHE_HOME"];
        result["TMPDIR"] = Path.Combine(root, "temp");
        result["TMP"] = result["TMPDIR"];
        result["TEMP"] = result["TMPDIR"];
        foreach (var name in new[] { "HOME", "GROK_HOME", "XDG_CONFIG_HOME", "XDG_CACHE_HOME", "TMPDIR" })
        {
            Directory.CreateDirectory(result[name]);
        }
        result["GROK_DISABLE_AUTOUPDATER"] = "1";
        result["CI"] = "true";
        return result;
    }
}
