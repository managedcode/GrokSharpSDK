using System.Text.Json;
using ManagedCode.GrokSharpSDK.Extensions.AI;
using ManagedCode.GrokSharpSDK.Extensions.AI.Extensions;
using ManagedCode.GrokSharpSDK.Models;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace ManagedCode.GrokSharpSDK.Tests;

[NotInParallel(NativeFixture.ProcessConstraint)]
public sealed class ChatClientTests
{
    [Test]
    public async Task DependencyInjection_StreamingAndResumeReachNativeAgent()
    {
        await using var fixture = await NativeFixture.CreateAsync();
        var services = new ServiceCollection();
        services.AddGrokChatClient(options =>
        {
            options.ClientOptions = fixture.ClientOptions;
            options.RunOptions = fixture.RunOptions;
        });
        services.AddKeyedGrokChatClient("grok", options =>
        {
            options.ClientOptions = fixture.ClientOptions;
            options.RunOptions = fixture.RunOptions;
        });
        using var provider = services.BuildServiceProvider();
        var client = provider.GetRequiredService<IChatClient>();
        using var deadline = new CancellationTokenSource(NativeFixture.RunDeadline);
        var updates = new List<ChatResponseUpdate>();
        await foreach (var update in client.GetStreamingResponseAsync(
            [new ChatMessage(ChatRole.User, "adapter-first-turn-673")], cancellationToken: deadline.Token))
        {
            updates.Add(update);
        }
        await Assert.That(string.Concat(updates.Select(update => update.Text))).IsEqualTo(NativeFixture.Answer);
        var textUpdates = updates.Where(update => !string.IsNullOrEmpty(update.Text)).ToArray();
        await Assert.That(textUpdates.Length).IsEqualTo(2);
        await Assert.That(textUpdates[0].Text).IsEqualTo("SDK ");
        var terminal = updates[^1];
        await Assert.That(terminal.ConversationId).IsNotNull();
        await Assert.That(terminal.FinishReason).IsEqualTo(ChatFinishReason.Stop);
        var usage = terminal.Contents.OfType<UsageContent>().Single().Details;
        await Assert.That(usage.TotalTokenCount).IsEqualTo(15L);
        var keyed = provider.GetRequiredKeyedService<IChatClient>("grok");
        fixture.Requests.Clear();
        var response = await keyed.GetResponseAsync(
            [new ChatMessage(ChatRole.User, "adapter-first-turn-673"),
             new ChatMessage(ChatRole.Assistant, NativeFixture.Answer),
             new ChatMessage(ChatRole.User, "adapter-second-turn-814")],
            new ChatOptions { ConversationId = terminal.ConversationId }, deadline.Token);
        await Assert.That(response.ConversationId).IsEqualTo(terminal.ConversationId);
        await Assert.That(response.Text).IsEqualTo(NativeFixture.Answer);
        await Assert.That(response.RawRepresentation is GrokRunResult).IsTrue();
        await Assert.That(fixture.Requests.Any(request => Count(request, "adapter-first-turn-673") == 1 &&
            request.Contains("adapter-second-turn-814", StringComparison.Ordinal))).IsTrue();
    }

    [Test]
    public async Task StructuredOutput_MapsValidatedNativeJsonForBothModes()
    {
        await using var fixture = await NativeFixture.CreateAsync(ModelScenario.Schema);
        using var client = new GrokChatClient(new() { ClientOptions = fixture.ClientOptions, RunOptions = fixture.RunOptions });
        using var schema = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Fixtures", "answer-schema.json")));
        var options = new ChatOptions { ResponseFormat = new ChatResponseFormatJson(schema.RootElement) };
        using var deadline = new CancellationTokenSource(NativeFixture.RunDeadline);
        var response = await client.GetResponseAsync([new ChatMessage(ChatRole.User, "Give a fixture answer as JSON.")], options, deadline.Token);
        using var result = JsonDocument.Parse(response.Text);
        await Assert.That(result.RootElement.GetProperty("answer").GetString()).IsEqualTo("fixture");
        var updates = new List<ChatResponseUpdate>();
        await foreach (var update in client.GetStreamingResponseAsync(
            [new ChatMessage(ChatRole.User, "Give a fixture answer as JSON.")], options, deadline.Token))
        {
            updates.Add(update);
        }
        using var streamed = JsonDocument.Parse(string.Concat(updates.Select(update => update.Text)));
        await Assert.That(streamed.RootElement.GetProperty("answer").GetString()).IsEqualTo("fixture");
        await Assert.That(updates.Count(update => !string.IsNullOrEmpty(update.Text))).IsEqualTo(1);
    }

    [Test]
    public async Task UnsupportedOptions_RejectBeforeExecution_ThenValidCallSucceeds()
    {
        await using var fixture = await NativeFixture.CreateAsync();
        using var client = new GrokChatClient(new() { ClientOptions = fixture.ClientOptions, RunOptions = fixture.RunOptions });
        var messages = new[] { new ChatMessage(ChatRole.User, "validation probe") };
        await Assert.ThrowsAsync<NotSupportedException>(async () =>
            await client.GetResponseAsync(messages, new ChatOptions { Temperature = 0.3f }));
        await Assert.ThrowsAsync<NotSupportedException>(async () =>
            await client.GetResponseAsync([new ChatMessage(ChatRole.User, [new DataContent(new byte[] { 1 }, "image/png")])]));
        await Assert.ThrowsAsync<NotSupportedException>(async () =>
            await client.GetResponseAsync(messages, new ChatOptions { ResponseFormat = ChatResponseFormat.Json }));
        await Assert.That(fixture.Requests.IsEmpty).IsTrue();
        using var deadline = new CancellationTokenSource(NativeFixture.RunDeadline);
        var response = await client.GetResponseAsync(messages, cancellationToken: deadline.Token);
        await Assert.That(response.Text).IsEqualTo(NativeFixture.Answer);
    }

    [Test]
    public async Task SharedClient_ExecutesConcurrentIndependentConversations()
    {
        await using var fixture = await NativeFixture.CreateAsync();
        using var client = new GrokChatClient(new() { ClientOptions = fixture.ClientOptions, RunOptions = fixture.RunOptions });
        using var deadline = new CancellationTokenSource(NativeFixture.RunDeadline);
        var responses = await Task.WhenAll(
            client.GetResponseAsync([new ChatMessage(ChatRole.User, "parallel one")], cancellationToken: deadline.Token),
            client.GetResponseAsync([new ChatMessage(ChatRole.User, "parallel two")], cancellationToken: deadline.Token));
        await Assert.That(responses[0].Text).IsEqualTo(NativeFixture.Answer);
        await Assert.That(responses[1].Text).IsEqualTo(NativeFixture.Answer);
        await Assert.That(responses[0].ConversationId == responses[1].ConversationId).IsFalse();
    }

    [Test]
    public async Task NativeTool_StreamingEmitsSafeActivityAndCompletes()
    {
        await using var fixture = await NativeFixture.CreateAsync(ModelScenario.ReadFile);
        using var client = new GrokChatClient(new() { ClientOptions = fixture.ClientOptions, RunOptions = fixture.RunOptions });
        using var deadline = new CancellationTokenSource(NativeFixture.RunDeadline);
        var updates = new List<ChatResponseUpdate>();
        await foreach (var update in client.GetStreamingResponseAsync(
            [new ChatMessage(ChatRole.User, "Read canary.txt with read_file then answer.")], cancellationToken: deadline.Token))
        {
            updates.Add(update);
        }
        await Assert.That(updates.Any(update => update.AdditionalProperties?.TryGetValue("managedcode:activity", out var value) == true &&
            value is string text && text == "native_tool")).IsTrue();
        await Assert.That(string.Concat(updates.Select(update => update.Text))).IsEqualTo(NativeFixture.Answer);
        await Assert.That(fixture.Requests.Any(request => request.Contains(NativeFixture.FileMarker, StringComparison.Ordinal))).IsTrue();
    }

    private static int Count(string text, string value) => text.Split(value, StringSplitOptions.None).Length - 1;
}
