using System.Text.Json;
using System.Text.Json.Serialization;
using ManagedCode.GrokSharpSDK.Models;

namespace ManagedCode.GrokSharpSDK.Internal;

internal static class NativeEventParser
{
    internal const string TextEvent = "text";
    internal const string EndEvent = "end";
    internal const string ErrorEvent = "error";
    internal const string CancelledReason = "cancelled";
    private const string MissingTypeMessage = "Grok emitted a native event without a type.";

    internal static GrokEvent Parse(string line)
    {
        using var document = JsonDocument.Parse(line);
        var root = document.RootElement;
        return new GrokEvent
        {
            Type = GetString(root, "type") ?? throw new InvalidDataException(MissingTypeMessage),
            Text = GetString(root, "data"),
            SessionId = GetString(root, "sessionId"),
            RequestId = GetString(root, "requestId"),
            StopReason = GetString(root, "stopReason"),
            Usage = root.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object
                ? usage.Deserialize(GrokJsonContext.Default.GrokUsage) : null,
            StructuredOutput = root.TryGetProperty("structuredOutput", out var structured) &&
                structured.ValueKind != JsonValueKind.Null ? structured.Clone() : null,
            UsageIsIncomplete = root.TryGetProperty("usage_is_incomplete", out var incomplete) && incomplete.GetBoolean(),
            Raw = root.Clone(),
        };
    }

    internal static string? GetString(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}

[JsonSerializable(typeof(GrokUsage))]
internal sealed partial class GrokJsonContext : JsonSerializerContext;
