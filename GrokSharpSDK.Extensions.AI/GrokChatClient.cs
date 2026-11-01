using System.Runtime.CompilerServices;
using ManagedCode.GrokSharpSDK.Client;
using ManagedCode.GrokSharpSDK.Extensions.AI.Internal;
using ManagedCode.GrokSharpSDK.Models;
using Microsoft.Extensions.AI;

namespace ManagedCode.GrokSharpSDK.Extensions.AI;

public sealed class GrokChatClient : IChatClient
{
    private readonly GrokClient _client;
    private readonly GrokRunOptions _defaults;
    private readonly ChatClientMetadata _metadata;

    public GrokChatClient(GrokChatClientOptions? options = null)
    {
        options ??= new();
        ArgumentNullException.ThrowIfNull(options.ClientOptions);
        ArgumentNullException.ThrowIfNull(options.RunOptions);
        _defaults = options.RunOptions with { AdditionalArguments = [.. options.RunOptions.AdditionalArguments] };
        _client = new GrokClient(options.ClientOptions);
        _metadata = new ChatClientMetadata("xai", defaultModelId: _defaults.Model);
    }

    public async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages,
        ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        var input = ChatInputMapper.Map(messages, options, _defaults, this);
        var result = await _client.RunAsync(input.Prompt, input.Options, cancellationToken).ConfigureAwait(false);
        return new ChatResponse(new ChatMessage(ChatRole.Assistant,
            result.StructuredOutput?.GetRawText() ?? result.Text))
        {
            ConversationId = result.SessionId,
            ResponseId = result.RequestId,
            ModelId = input.Options.Model,
            FinishReason = ChatResultMapper.FinishReason(result.StopReason),
            Usage = ChatResultMapper.Usage(result.Usage),
            RawRepresentation = result,
        };
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
        ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var input = ChatInputMapper.Map(messages, options, _defaults, this);
        await foreach (var update in _client.RunStreamingAsync(input.Prompt, input.Options, cancellationToken).ConfigureAwait(false))
        {
            var mapped = new ChatResponseUpdate
            {
                Role = ChatRole.Assistant,
                ModelId = input.Options.Model,
                RawRepresentation = update,
            };
            if (update.Type == "text" && input.Options.JsonSchema is null)
            {
                mapped.Contents.Add(new TextContent(update.Text));
            }
            else if (update.Type == "thought")
            {
                mapped.Contents.Add(new TextReasoningContent(update.Text));
            }
            else if (update.Type == "end")
            {
                mapped.ConversationId = update.SessionId;
                mapped.ResponseId = update.RequestId;
                mapped.FinishReason = ChatResultMapper.FinishReason(update.StopReason!);
                if (update.StructuredOutput is { } structured)
                {
                    mapped.Contents.Add(new TextContent(structured.GetRawText()));
                }
                if (ChatResultMapper.Usage(update.Usage) is { } usage)
                {
                    mapped.Contents.Add(new UsageContent(usage));
                }
            }
            yield return mapped;
        }
    }

    public object? GetService(Type serviceType, object? serviceKey = null)
    {
        ArgumentNullException.ThrowIfNull(serviceType);
        if (serviceKey is not null)
        {
            return null;
        }
        if (serviceType == typeof(ChatClientMetadata))
        {
            return _metadata;
        }
        if (serviceType.IsInstanceOfType(this))
        {
            return this;
        }
        return serviceType.IsInstanceOfType(_client) ? _client : null;
    }

    public void Dispose() => _client.Dispose();
}
