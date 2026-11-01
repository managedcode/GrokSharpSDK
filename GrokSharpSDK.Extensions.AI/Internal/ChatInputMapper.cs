using ManagedCode.GrokSharpSDK.Models;
using Microsoft.Extensions.AI;

namespace ManagedCode.GrokSharpSDK.Extensions.AI.Internal;

internal static class ChatInputMapper
{
    private static readonly ChatRole DeveloperRole = new("developer");
    private const string UnsupportedOptions = "The selected MEAI options are not supported by Grok Build CLI. Use GrokRunOptions through RawRepresentationFactory for native CLI options.";

    internal static (string Prompt, GrokRunOptions Options) Map(IEnumerable<ChatMessage> messages,
        ChatOptions? options, GrokRunOptions defaults, IChatClient client)
    {
        ArgumentNullException.ThrowIfNull(messages);
        Validate(options);
        var native = options?.RawRepresentationFactory?.Invoke(client) switch
        {
            null => defaults,
            GrokRunOptions raw => raw,
            _ => throw new NotSupportedException("RawRepresentationFactory must return GrokRunOptions."),
        };
        var conversationId = options?.ConversationId ?? native.ResumeSessionId;
        var instructions = new List<string>();
        if (!string.IsNullOrWhiteSpace(native.Instructions))
        {
            instructions.Add(native.Instructions);
        }
        if (!string.IsNullOrWhiteSpace(options?.Instructions))
        {
            instructions.Add(options.Instructions);
        }
        var turns = new List<(ChatRole Role, string Text)>();
        foreach (var message in messages)
        {
            if (message.Role != ChatRole.User && message.Role != ChatRole.Assistant &&
                message.Role != ChatRole.System && message.Role != DeveloperRole)
            {
                throw new NotSupportedException("Only user, assistant, system and developer text messages are supported.");
            }
            if (message.Contents.Any(content => content is not TextContent))
            {
                throw new NotSupportedException("GrokChatClient accepts text content only. Native Grok tools run inside the CLI.");
            }
            var text = string.Concat(message.Contents.OfType<TextContent>().Select(content => content.Text));
            if (message.Role == ChatRole.System || message.Role == DeveloperRole)
            {
                instructions.Add(text);
            }
            else
            {
                turns.Add((message.Role, text));
            }
        }
        if (turns.Count == 0 || turns[^1].Role != ChatRole.User)
        {
            throw new ArgumentException("The conversation must end with a user text message.", nameof(messages));
        }
        if (conversationId is not null)
        {
            var lastAssistant = turns.FindLastIndex(turn => turn.Role == ChatRole.Assistant);
            if (lastAssistant >= 0)
            {
                turns.RemoveRange(0, lastAssistant + 1);
            }
        }
        var prompt = turns.Count == 1 ? turns[0].Text
            : string.Join(Environment.NewLine + Environment.NewLine,
                turns.Select(turn => string.Concat(turn.Role.Value, ":\n", turn.Text)));
        ArgumentException.ThrowIfNullOrWhiteSpace(prompt);
        return (prompt, native with
        {
            Model = options?.ModelId ?? native.Model,
            ResumeSessionId = conversationId,
            Instructions = instructions.Count == 0 ? null : string.Join(Environment.NewLine, instructions),
            JsonSchema = options?.ResponseFormat switch { ChatResponseFormatJson json => json.Schema, ChatResponseFormatText => null, _ => native.JsonSchema },
            AdditionalArguments = [.. native.AdditionalArguments],
        });
    }

    private static void Validate(ChatOptions? options)
    {
        if (options is null)
        {
            return;
        }
        if (options.Temperature is not null || options.MaxOutputTokens is not null ||
            options.TopP is not null || options.TopK is not null || options.FrequencyPenalty is not null ||
            options.PresencePenalty is not null || options.Seed is not null || options.Reasoning is not null ||
            options.StopSequences is { Count: > 0 } || options.AllowMultipleToolCalls is not null ||
            options.ToolMode is not null || options.Tools is { Count: > 0 } ||
            options.AllowBackgroundResponses is true || options.ContinuationToken is not null ||
            options.AdditionalProperties is { Count: > 0 })
        {
            throw new NotSupportedException(UnsupportedOptions);
        }
        if (options.ResponseFormat is ChatResponseFormatJson { Schema: null })
        {
            throw new NotSupportedException("Grok Build requires an explicit JSON schema for structured output.");
        }
        if (options.ResponseFormat is not null && options.ResponseFormat is not ChatResponseFormatText &&
            options.ResponseFormat is not ChatResponseFormatJson)
        {
            throw new NotSupportedException(UnsupportedOptions);
        }
    }
}
