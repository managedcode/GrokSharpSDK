using System.Text.Json;
using ManagedCode.GrokSharpSDK.Client;
using ManagedCode.GrokSharpSDK.Models;

namespace ManagedCode.GrokSharpSDK.Tests;

public sealed class NativeAgentTests
{
    [Test]
    public async Task RunAndResume_UsesNativePersistenceAndUsage()
    {
        await using var fixture = await NativeFixture.CreateAsync();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        GrokRunResult first;
        using (var client = new GrokClient(fixture.ClientOptions))
        {
            first = await client.RunAsync("remember first-native-turn-459", fixture.RunOptions, deadline.Token);
        }
        await Assert.That(first.Text).IsEqualTo(NativeFixture.Answer);
        await Assert.That(first.Usage!.InputTokens).IsEqualTo(12L);
        await Assert.That(first.Usage.OutputTokens).IsEqualTo(3L);
        await Assert.That(first.Usage.TotalTokens).IsEqualTo(15L);
        await Assert.That(first.StopReason).IsEqualTo("end_turn");
        fixture.Requests.Clear();
        using var resumedClient = new GrokClient(fixture.ClientOptions);
        var second = await resumedClient.RunAsync("continue second-native-turn-638",
            fixture.RunOptions with { ResumeSessionId = first.SessionId }, deadline.Token);
        await Assert.That(second.SessionId).IsEqualTo(first.SessionId);
        await Assert.That(second.Text).IsEqualTo(NativeFixture.Answer);
        await Assert.That(fixture.Requests.Any(request => request.Contains("first-native-turn-459", StringComparison.Ordinal)
            && request.Contains("second-native-turn-638", StringComparison.Ordinal))).IsTrue();
        await Assert.That(Directory.GetFiles(fixture.InputDirectory)).IsEmpty();
    }

    [Test]
    public async Task NativeTool_ReadsRealFileAndCompletes()
    {
        await using var fixture = await NativeFixture.CreateAsync(ModelScenario.ReadFile);
        using var client = new GrokClient(fixture.ClientOptions);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var events = new List<GrokEvent>();
        await foreach (var update in client.RunStreamingAsync("Read canary.txt with read_file then answer.", fixture.RunOptions, deadline.Token))
        {
            events.Add(update);
        }
        await Assert.That(events[^1].Type).IsEqualTo("end");
        await Assert.That(string.Concat(events.Where(update => update.Type == "text").Select(update => update.Text))).IsEqualTo(NativeFixture.Answer);
        await Assert.That(events.Any(update => update.Type is "tool_call" or "tool_call_update")).IsTrue();
        await Assert.That(fixture.Requests.Any(request => ContainsToolResult(request, NativeFixture.FileMarker))).IsTrue();
    }

    [Test]
    public async Task ProviderFailure_ThrowsAndRemovesPrompt()
    {
        await using var fixture = await NativeFixture.CreateAsync(ModelScenario.Unauthorized);
        using var client = new GrokClient(fixture.ClientOptions);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await client.RunAsync("failure probe", fixture.RunOptions, deadline.Token));
        await Assert.That(fixture.Requests.IsEmpty).IsFalse();
        await Assert.That(Directory.GetFiles(fixture.InputDirectory)).IsEmpty();
    }

    [Test]
    public async Task Cancellation_TerminatesNativeRequestAndRemovesPrompt()
    {
        await using var fixture = await NativeFixture.CreateAsync(ModelScenario.Hold);
        using var client = new GrokClient(fixture.ClientOptions);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var run = client.RunAsync("cancellation probe", fixture.RunOptions, cancellation.Token);
        await fixture.Started.Task.WaitAsync(cancellation.Token);
        await cancellation.CancelAsync();
        await Assert.ThrowsAsync<OperationCanceledException>(async () => await run);
        await fixture.Disconnected.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await Assert.That(Directory.GetFiles(fixture.InputDirectory)).IsEmpty();
    }

    [Test]
    public async Task StreamingEarlyDispose_TerminatesRequestAndRemovesPrompt()
    {
        await using var fixture = await NativeFixture.CreateAsync(ModelScenario.HoldAfterText);
        using var client = new GrokClient(fixture.ClientOptions);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await foreach (var update in client.RunStreamingAsync("early dispose probe", fixture.RunOptions, deadline.Token))
        {
            if (update.Type == "text")
            {
                await Assert.That(update.Text).IsEqualTo("SDK ");
                break;
            }
        }
        await fixture.Disconnected.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await Assert.That(Directory.GetFiles(fixture.InputDirectory)).IsEmpty();
    }

    private static bool ContainsToolResult(string request, string marker)
    {
        using var document = JsonDocument.Parse(request);
        return document.RootElement.GetProperty("messages").EnumerateArray().Any(message =>
            message.GetProperty("role").GetString() == "tool" && message.GetRawText().Contains(marker, StringComparison.Ordinal));
    }
}
