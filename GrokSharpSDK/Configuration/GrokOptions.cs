using ManagedCode.GrokSharpSDK.Internal;
using ManagedCode.GrokSharpSDK.Models;

namespace ManagedCode.GrokSharpSDK.Configuration;

public sealed record GrokOptions
{
    public string? ExecutablePath { get; init; }
    public string? WorkingDirectory { get; init; }
    public CliLaunchCommand? LaunchCommand { get; init; }
    public IReadOnlyDictionary<string, string>? EnvironmentVariables { get; init; }
    public bool InheritEnvironmentVariables { get; init; } = true;
    public string TemporaryDirectory { get; init; } = Path.GetTempPath();
    // Same bounded shutdown allowance as the sibling ManagedCode CLI SDKs.
    public TimeSpan ProcessTerminationTimeout { get; init; } = TimeSpan.FromSeconds(5);

    public CliLaunchCommand GetCliLaunchCommand()
    {
        var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var path = EnvironmentVariables?.FirstOrDefault(pair => comparer.Equals(pair.Key, CliResolver.PathVariable)).Value;
        path ??= InheritEnvironmentVariables ? Environment.GetEnvironmentVariable(CliResolver.PathVariable) : null;
        return LaunchCommand ?? CliResolver.Resolve(ExecutablePath, path);
    }

    internal Dictionary<string, string> CreateEnvironment()
    {
        var result = new Dictionary<string, string>(OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        if (InheritEnvironmentVariables)
        {
            foreach (System.Collections.DictionaryEntry pair in Environment.GetEnvironmentVariables())
            {
                result[(string)pair.Key] = (string)pair.Value!;
            }
        }
        if (EnvironmentVariables is not null)
        {
            foreach (var pair in EnvironmentVariables)
            {
                result[pair.Key] = pair.Value;
            }
        }
        return result;
    }
}
