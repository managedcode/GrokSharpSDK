namespace ManagedCode.GrokSharpSDK.Models;

public sealed record GrokCliModel(string Id);
public sealed record GrokCliMetadata(string Version, string DefaultModel, IReadOnlyList<GrokCliModel> Models);

/// <summary>A native operation failed after its owned cleanup was attempted.</summary>
public sealed class CliExecutionFailureException(string message, bool rootProcessExitConfirmed, Exception? innerException = null)
    : InvalidOperationException(message, innerException)
{
    public bool RootProcessExitConfirmed { get; } = rootProcessExitConfirmed;
}

public enum CliPackageManager { Npm, Bun }
public enum CliInstallationStage { PackageManagerStarted, OutputObserved, Verifying, Installed }
public sealed record CliInstallationOptions
{
    public required CliPackageManager PackageManager { get; init; }
    public required string PackageManagerExecutablePath { get; init; }
    public string? NpmCliScriptPath { get; init; }
    public required IReadOnlyDictionary<string, string> EnvironmentVariables { get; init; }
    public required TimeSpan InstallTimeout { get; init; }
    public required TimeSpan ProcessTerminationTimeout { get; init; }
    public required int MaximumOutputCharacters { get; init; }
    /// <summary>Optional existing parent for SDK-owned unique installation directories.</summary>
    public string? InstallationDirectory { get; init; }
}
public sealed record CliInstallationResult(string InstalledVersion, string TargetVersion,
    string InstallationRootPath, CliLaunchCommand LaunchCommand);
public sealed record CliInstallationUpdate(CliInstallationStage Stage,
    int StandardOutputCharactersObserved = 0, int StandardErrorCharactersObserved = 0,
    CliInstallationResult? Result = null);
