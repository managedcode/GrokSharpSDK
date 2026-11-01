using ManagedCode.GrokSharpSDK.Models;
using Microsoft.Extensions.AI;

namespace ManagedCode.GrokSharpSDK.Extensions.AI.Internal;

internal static class ChatResultMapper
{
    internal static ChatFinishReason FinishReason(string reason) => reason switch
    {
        "end_turn" => ChatFinishReason.Stop,
        "max_tokens" => ChatFinishReason.Length,
        _ => new ChatFinishReason(reason),
    };

    internal static UsageDetails? Usage(GrokUsage? usage) => usage is null ? null : new UsageDetails
    {
        InputTokenCount = usage.FullInputTokens,
        OutputTokenCount = usage.OutputTokens,
        TotalTokenCount = usage.TotalTokens ?? usage.FullInputTokens + usage.OutputTokens,
        CachedInputTokenCount = usage.CacheReadInputTokens,
        ReasoningTokenCount = usage.ReasoningTokens,
    };
}
