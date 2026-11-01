using ManagedCode.GrokSharpSDK.Client;
using ManagedCode.GrokSharpSDK.Models;

namespace ManagedCode.GrokSharpSDK.Tests;

[NotInParallel(NativeFixture.ProcessConstraint)]
public sealed class LoginTests
{
    [Test]
    public async Task AlreadyCancelled_DoesNotStartLoginOrWriteCredentials()
    {
        await using var fixture = await NativeFixture.CreateAsync();
        using var client = new GrokClient(fixture.ClientOptions);
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        await Assert.ThrowsAsync<OperationCanceledException>(async () => await client.BeginLoginAsync(cancellation.Token));
        await Assert.That(fixture.HasCredentials).IsFalse();
    }

    [Test]
    public async Task NativeVersionExit_DoesNotReportAuthenticationSuccess()
    {
        await using var fixture = await NativeFixture.CreateAsync();
        var launch = fixture.ClientOptions.GetCliLaunchCommand();
        using var client = new GrokClient(fixture.ClientOptions with
        {
            LaunchCommand = new CliLaunchCommand(launch.ExecutablePath, [.. launch.PrefixArguments, "--version"]),
        });
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await client.BeginLoginAsync(deadline.Token));
        await Assert.That(fixture.HasCredentials).IsFalse();
    }

    [Test, Property("RequiresGrokAuth", "true")]
    public async Task DeviceInstructions_ObservationCancellationDoesNotCancelLogin()
    {
        await using var fixture = await NativeFixture.CreateAsync();
        using var client = new GrokClient(fixture.ClientOptions);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await using var login = await client.BeginLoginAsync(deadline.Token);
        await Assert.That(login.Instructions.LoginUrl.Scheme).IsEqualTo("https");
        await Assert.That(string.IsNullOrWhiteSpace(login.Instructions.UserCode)).IsFalse();
        using var observer = new CancellationTokenSource();
        await observer.CancelAsync();
        await Assert.ThrowsAsync<OperationCanceledException>(async () => await login.WaitForCompletionAsync(observer.Token));
        await login.CancelAsync(deadline.Token);
        await Assert.ThrowsAsync<OperationCanceledException>(async () => await login.WaitForCompletionAsync());
        await Assert.That(fixture.HasCredentials).IsFalse();
    }
}
