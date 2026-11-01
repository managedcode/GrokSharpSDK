using System.Text.Json;
using System.Text.Json.Serialization;
using ManagedCode.GrokSharpSDK.Client;
using ManagedCode.GrokSharpSDK.Models;

namespace ManagedCode.GrokSharpSDK.Tests;

[NotInParallel(NativeFixture.ProcessConstraint)]
public sealed class ManagementTests
{
    // Reuses the consuming Worker's documented metadata/output budget.
    private const int OutputBudget = 8388608;

    [Test]
    public async Task NativeMetadata_ReloadedLaunchRunsSelectedModelWithoutDiscoveryInference()
    {
        await using var fixture = await NativeFixture.CreateAsync();
        using var client = new GrokClient(fixture.ClientOptions);
        var metadata = client.GetCliMetadata(NativeFixture.RunDeadline, OutputBudget);
        await Assert.That(metadata.Version).IsEqualTo(GrokCliCompatibility.TargetVersion);
        await Assert.That(metadata.Models.Any(model => model.Id == fixture.RunOptions.Model)).IsTrue();
        await Assert.That(fixture.Requests.IsEmpty).IsTrue();
        var serialized = JsonSerializer.Serialize(client.GetCliLaunchCommand(), ManagementJsonContext.Default.CliLaunchCommand);
        var launch = JsonSerializer.Deserialize(serialized, ManagementJsonContext.Default.CliLaunchCommand)!;
        using var reloaded = new GrokClient(fixture.ClientOptions with { LaunchCommand = launch });
        using var deadline = new CancellationTokenSource(NativeFixture.RunDeadline);
        var result = await reloaded.RunAsync("Run discovered local model.", fixture.RunOptions, deadline.Token);
        await Assert.That(result.Text).IsEqualTo(NativeFixture.Answer);
    }

    [Test]
    [Arguments(CliPackageManager.Npm)]
    [Arguments(CliPackageManager.Bun)]
    public async Task ManagedInstallation_VerifiesNativeGenerationThenExecutes(CliPackageManager manager)
    {
        await using var fixture = await NativeFixture.CreateAsync();
        using var client = new GrokClient(fixture.ClientOptions);
        var options = CreateInstallationOptions(fixture, manager);
        using var deadline = new CancellationTokenSource(options.InstallTimeout);
        CliInstallationResult? installed = null;
        await foreach (var update in client.InstallOrUpdateCliAsync(options, deadline.Token))
        {
            installed = update.Result ?? installed;
        }
        await Assert.That(installed).IsNotNull();
        await Assert.That(installed!.InstalledVersion).IsEqualTo(GrokCliCompatibility.TargetVersion);
        var serialized = JsonSerializer.Serialize(installed.LaunchCommand, ManagementJsonContext.Default.CliLaunchCommand);
        var launch = JsonSerializer.Deserialize(serialized, ManagementJsonContext.Default.CliLaunchCommand)!;
        using var native = new GrokClient(fixture.ClientOptions with { LaunchCommand = launch });
        var result = await native.RunAsync("Execute installed generation.", fixture.RunOptions, deadline.Token);
        await Assert.That(result.Text).IsEqualTo(NativeFixture.Answer);
        await Assert.That(fixture.HasCredentials).IsFalse();
    }

    [Test]
    [Arguments(CliPackageManager.Npm)]
    [Arguments(CliPackageManager.Bun)]
    public async Task ManagedInstallation_EarlyDisposeJoinsAndRemovesUnverifiedGeneration(CliPackageManager manager)
    {
        await using var fixture = await NativeFixture.CreateAsync();
        using var client = new GrokClient(fixture.ClientOptions);
        var options = CreateInstallationOptions(fixture, manager);
        var reachedVerification = false;
        await foreach (var update in client.InstallOrUpdateCliAsync(options))
        {
            await Assert.That(update.Result).IsNull();
            if (update.Stage == CliInstallationStage.Verifying)
            {
                reachedVerification = true;
                break;
            }
        }
        await Assert.That(reachedVerification).IsTrue();
        await Assert.That(Directory.EnumerateFiles(options.InstallationDirectory!, "*", SearchOption.AllDirectories)).IsEmpty();
    }

    [Test]
    public async Task ManagedInstallation_DeadlineReportsConfirmedFailureAndRemovesGeneration()
    {
        await using var fixture = await NativeFixture.CreateAsync();
        using var client = new GrokClient(fixture.ClientOptions);
        // Force the real package manager to exceed the installation budget; no fake process is substituted.
        var options = CreateInstallationOptions(fixture, CliPackageManager.Npm) with { InstallTimeout = TimeSpan.FromMilliseconds(1) };
        CliExecutionFailureException? failure = null;
        try
        {
            await foreach (var _ in client.InstallOrUpdateCliAsync(options)) { }
        }
        catch (CliExecutionFailureException exception) { failure = exception; }
        await Assert.That(failure).IsNotNull();
        await Assert.That(failure!.RootProcessExitConfirmed).IsTrue();
        await Assert.That(failure.InnerException is TimeoutException).IsTrue();
        await Assert.That(Directory.EnumerateFiles(options.InstallationDirectory!, "*", SearchOption.AllDirectories)).IsEmpty();
    }

    private static CliInstallationOptions CreateInstallationOptions(NativeFixture fixture, CliPackageManager manager)
    {
        var executable = FindExecutable(manager == CliPackageManager.Npm ? "node" : "bun");
        var directory = Path.GetDirectoryName(executable)!;
        string? npm = null;
        if (manager == CliPackageManager.Npm)
        {
            var npmCommand = Path.Combine(directory, "npm");
            npm = File.Exists(npmCommand) && new FileInfo(npmCommand).ResolveLinkTarget(true) is { } target
                ? target.FullName
                : new[] { Path.Combine(directory, "node_modules", "npm", "bin", "npm-cli.js"),
                    Path.GetFullPath(Path.Combine(directory, "..", "lib", "node_modules", "npm", "bin", "npm-cli.js")) }
                    .First(File.Exists);
        }
        var environment = new Dictionary<string, string> { ["PATH"] = Environment.GetEnvironmentVariable("PATH")! };
        if (Environment.GetEnvironmentVariable("SystemRoot") is { } systemRoot)
        {
            environment["SYSTEMROOT"] = systemRoot;
        }
        return new()
        {
            PackageManager = manager,
            PackageManagerExecutablePath = executable,
            NpmCliScriptPath = npm,
            EnvironmentVariables = environment,
            InstallationDirectory = Path.Combine(fixture.Root, "installations"),
            InstallTimeout = TimeSpan.FromMinutes(5), // Native install + Windows cold turn, not a runtime product limit.
            ProcessTerminationTimeout = fixture.ClientOptions.ProcessTerminationTimeout,
            MaximumOutputCharacters = OutputBudget,
        };
    }

    private static string FindExecutable(string name)
    {
        var file = OperatingSystem.IsWindows() ? name + ".exe" : name;
        return Environment.GetEnvironmentVariable("PATH")!.Split(Path.PathSeparator)
            .Select(directory => Path.Combine(directory, file)).First(File.Exists);
    }
}

[JsonSerializable(typeof(CliLaunchCommand))]
[JsonSerializable(typeof(List<string>))]
internal sealed partial class ManagementJsonContext : JsonSerializerContext;
