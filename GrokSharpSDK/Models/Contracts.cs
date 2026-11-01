using System.Text.Json;
using System.Text.Json.Serialization;

namespace ManagedCode.GrokSharpSDK.Models;

public static class GrokCliCompatibility
{
    public const string TargetVersion = "1.0.70";
}

public sealed class CliLaunchCommand
{
    public CliLaunchCommand(string executablePath, params string[] prefixArguments)
        : this(executablePath, (IReadOnlyList<string>)prefixArguments) { }

    [JsonConstructor]
    public CliLaunchCommand(string executablePath, IReadOnlyList<string> prefixArguments)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        ArgumentNullException.ThrowIfNull(prefixArguments);
        if (!Path.IsPathFullyQualified(executablePath) || !File.Exists(executablePath))
        {
            throw new FileNotFoundException("LaunchCommand requires an existing absolute executable path.", executablePath);
        }
        ExecutablePath = executablePath;
        PrefixArguments = Array.AsReadOnly(prefixArguments.ToArray());
    }
    public string ExecutablePath { get; }
    public IReadOnlyList<string> PrefixArguments { get; }
}

public sealed record GrokRunOptions
{
    public string? Model { get; init; }
    public string? WorkingDirectory { get; init; }
    public string? ResumeSessionId { get; init; }
    public string? Instructions { get; init; }
    public string? ReasoningEffort { get; init; }
    public JsonElement? JsonSchema { get; init; }
    public string[] AdditionalArguments { get; init; } = [];
}

public sealed record GrokUsage
{
    [JsonPropertyName("input_tokens")] public long InputTokens { get; init; }
    [JsonPropertyName("output_tokens")] public long OutputTokens { get; init; }
    [JsonPropertyName("cache_read_input_tokens")] public long CacheReadInputTokens { get; init; }
    [JsonPropertyName("cache_creation_input_tokens")] public long CacheCreationInputTokens { get; init; }
    [JsonPropertyName("reasoning_tokens")] public long ReasoningTokens { get; init; }
    [JsonPropertyName("total_tokens")] public long? TotalTokens { get; init; }
    public long FullInputTokens => InputTokens + CacheReadInputTokens + CacheCreationInputTokens;
}

public sealed record GrokEvent
{
    public required string Type { get; init; }
    public string? Text { get; init; }
    public string? SessionId { get; init; }
    public string? RequestId { get; init; }
    public string? StopReason { get; init; }
    public GrokUsage? Usage { get; init; }
    public JsonElement? StructuredOutput { get; init; }
    public bool UsageIsIncomplete { get; init; }
    public required JsonElement Raw { get; init; }
}

public sealed record GrokRunResult(string Text, string SessionId, string RequestId,
    string StopReason, GrokUsage? Usage, JsonElement? StructuredOutput, bool UsageIsIncomplete);

public sealed record CliLoginInstructions(Uri LoginUrl, string UserCode);
