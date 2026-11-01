using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using ManagedCode.GrokSharpSDK.Configuration;
using ManagedCode.GrokSharpSDK.Models;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ManagedCode.GrokSharpSDK.Tests;

internal enum ModelScenario { Text, ReadFile, Schema, Unauthorized, Hold, HoldAfterText }

internal sealed class NativeFixture : IAsyncDisposable
{
    internal const string ProcessConstraint = "NativeCli";
    // Windows qualification measured 51 seconds for one cold turn; scenarios can start two turns.
    internal static readonly TimeSpan RunDeadline = TimeSpan.FromMinutes(3);
    internal const string Answer = "SDK fixture response";
    internal const string FileMarker = "native-file-canary-8246";
    private readonly WebApplication _server;
    internal string Root { get; }
    internal string InputDirectory { get; }
    internal string Workspace { get; }
    internal string ToolFile { get; }
    internal GrokOptions ClientOptions { get; }
    internal GrokRunOptions RunOptions { get; }
    internal ConcurrentQueue<string> Requests { get; } = new();
    internal TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal TaskCompletionSource Disconnected { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal ModelScenario Scenario { get; set; }

    private NativeFixture(WebApplication server, string root, string endpoint, ModelScenario scenario)
    {
        _server = server;
        Root = root;
        Scenario = scenario;
        Workspace = Directory.CreateDirectory(Path.Combine(root, "workspace")).FullName;
        InputDirectory = Directory.CreateDirectory(Path.Combine(root, "inputs")).FullName;
        ToolFile = Path.Combine(Workspace, "canary.txt");
        File.WriteAllText(ToolFile, FileMarker);
        var home = Directory.CreateDirectory(Path.Combine(root, "home")).FullName;
        var grokHome = Directory.CreateDirectory(Path.Combine(root, "grok")).FullName;
        var config = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "config.toml"))
            .Replace("{{BASE_URL}}", endpoint, StringComparison.Ordinal);
        File.WriteAllText(Path.Combine(grokHome, "config.toml"), config);
        var environment = new Dictionary<string, string>
        {
            ["PATH"] = Environment.GetEnvironmentVariable("PATH") ?? string.Empty,
            ["HOME"] = home,
            ["USERPROFILE"] = home,
            ["XDG_CONFIG_HOME"] = home,
            ["XDG_DATA_HOME"] = home,
            ["XDG_CACHE_HOME"] = home,
            ["APPDATA"] = home,
            ["LOCALAPPDATA"] = home,
            ["GROK_HOME"] = grokHome,
            ["GROK_DISABLE_AUTOUPDATER"] = "1",
            ["GROK_MEMORY"] = "0",
            ["CI"] = "true",
            ["BROWSER"] = "false",
        };
        if (Environment.GetEnvironmentVariable("SystemRoot") is { } systemRoot)
        {
            environment["SystemRoot"] = systemRoot;
        }
        ClientOptions = new GrokOptions
        {
            ExecutablePath = ResolveExecutable(),
            WorkingDirectory = Workspace,
            EnvironmentVariables = environment,
            InheritEnvironmentVariables = false,
            TemporaryDirectory = InputDirectory,
        };
        RunOptions = new GrokRunOptions
        {
            Model = "sdk-fixture",
            WorkingDirectory = Workspace,
            AdditionalArguments = ["--no-plan", "--no-subagents", "--disable-web-search"],
        };
    }

    internal static async Task<NativeFixture> CreateAsync(ModelScenario scenario = ModelScenario.Text)
    {
        var repository = FindRepository();
        var root = Directory.CreateDirectory(Path.Combine(repository, "tests", ".sandbox",
            Guid.CreateVersion7().ToString("N"))).FullName;
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        var server = builder.Build();
        NativeFixture? fixture = null;
        server.MapPost("/v1/chat/completions", (HttpContext context) => fixture!.RespondAsync(context));
        server.MapGet("/{**path}", () => Results.Text(new JsonObject { ["data"] = new JsonArray() }.ToJsonString(), "application/json"));
        try
        {
            await server.StartAsync();
            var addresses = server.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!;
            fixture = new NativeFixture(server, root, addresses.Addresses.Single() + "/v1", scenario);
            return fixture;
        }
        catch
        {
            await server.DisposeAsync();
            Directory.Delete(root, recursive: true);
            throw;
        }
    }

    internal bool HasCredentials => Directory.EnumerateFiles(Path.Combine(Root, "grok"), "*",
        SearchOption.AllDirectories).Any(path => Path.GetFileName(path).Contains("credential", StringComparison.OrdinalIgnoreCase)
            || Path.GetFileName(path) == "auth.json");

    private async Task RespondAsync(HttpContext context)
    {
        using var document = await JsonDocument.ParseAsync(context.Request.Body, cancellationToken: context.RequestAborted);
        var request = document.RootElement;
        Requests.Enqueue(request.GetRawText());
        Started.TrySetResult();
        if (Scenario == ModelScenario.Unauthorized)
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await context.Response.WriteAsync(new JsonObject
            {
                ["error"] = new JsonObject
                { ["message"] = "Fixture authentication rejected", ["type"] = "invalid_api_key" }
            }.ToJsonString(),
                context.RequestAborted);
            return;
        }
        context.Response.ContentType = "text/event-stream";
        try
        {
            if (Scenario is ModelScenario.Hold or ModelScenario.HoldAfterText)
            {
                if (Scenario == ModelScenario.HoldAfterText)
                {
                    await ChunkAsync(new JsonObject { ["role"] = "assistant", ["content"] = "SDK " });
                }
                else
                {
                    await context.Response.StartAsync(context.RequestAborted);
                }
                await Task.Delay(Timeout.InfiniteTimeSpan, context.RequestAborted);
                return;
            }
            var hasToolResult = request.GetProperty("messages").EnumerateArray()
                .Any(message => message.GetProperty("role").GetString() == "tool");
            var readToolAvailable = request.TryGetProperty("tools", out var tools) &&
                tools.EnumerateArray().Any(tool => tool.GetProperty("function").GetProperty("name").GetString() == "read_file");
            if (Scenario == ModelScenario.ReadFile && !hasToolResult && readToolAvailable)
            {
                await ChunkAsync(new JsonObject
                {
                    ["role"] = "assistant",
                    ["tool_calls"] = new JsonArray(new JsonObject
                    {
                        ["index"] = 0,
                        ["id"] = "native-read-1",
                        ["type"] = "function",
                        ["function"] = new JsonObject
                        {
                            ["name"] = "read_file",
                            ["arguments"] = new JsonObject { ["target_file"] = ToolFile }.ToJsonString(),
                        },
                    }),
                });
                await ChunkAsync(new JsonObject(), "tool_calls", includeUsage: true);
            }
            else
            {
                var content = Scenario == ModelScenario.Schema ? """{"answer":"fixture"}""" : Answer;
                var split = Scenario == ModelScenario.Schema ? content.Length : 4;
                await ChunkAsync(new JsonObject { ["role"] = "assistant", ["content"] = content[..split] });
                if (split < content.Length)
                {
                    await ChunkAsync(new JsonObject { ["content"] = content[split..] });
                }
                await ChunkAsync(new JsonObject(), "stop", includeUsage: true);
            }
            await context.Response.WriteAsync("data: [DONE]\n\n", context.RequestAborted);
            await context.Response.Body.FlushAsync(context.RequestAborted);
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            Disconnected.TrySetResult();
        }
        catch (IOException) when (context.RequestAborted.IsCancellationRequested)
        {
            Disconnected.TrySetResult();
        }

        async Task ChunkAsync(JsonObject delta, string? finish = null, bool includeUsage = false)
        {
            var chunk = new JsonObject
            {
                ["id"] = "fixture-response",
                ["object"] = "chat.completion.chunk",
                ["created"] = 0,
                ["model"] = "sdk-fixture",
                ["choices"] = new JsonArray(new JsonObject { ["index"] = 0, ["delta"] = delta, ["finish_reason"] = finish }),
            };
            if (includeUsage)
            {
                chunk["usage"] = new JsonObject { ["prompt_tokens"] = 12, ["completion_tokens"] = 3, ["total_tokens"] = 15 };
            }
            await context.Response.WriteAsync("data: " + chunk.ToJsonString() + "\n\n", context.RequestAborted);
            await context.Response.Body.FlushAsync(context.RequestAborted);
        }
    }

    private static string ResolveExecutable()
    {
        if (Environment.GetEnvironmentVariable("GROK_TEST_EXECUTABLE") is { Length: > 0 } executable)
        {
            return executable;
        }
        var name = OperatingSystem.IsWindows() ? "grok.cmd" : "grok";
        var installed = Path.Combine(FindRepository(), "artifacts", "cli", "node_modules", ".bin", name);
        if (!File.Exists(installed))
        {
            throw new FileNotFoundException("Install the pinned real CLI into artifacts/cli or set GROK_TEST_EXECUTABLE.", installed);
        }
        return installed;
    }

    internal static string RepositoryRoot => FindRepository();

    private static string FindRepository()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "ManagedCode.GrokSharpSDK.slnx")))
        {
            directory = directory.Parent;
        }
        return directory?.FullName ?? throw new DirectoryNotFoundException("Repository root was not found.");
    }


    private void WriteStartupPhaseSummary()
    {
        // Export only labels from upstream's closed StartupPhase enum. Never export raw native records.
        string[] known = ["config_load", "managed_policy", "bootstrap", "model_catalog", "worker_spawn",
            "leader_connect", "acp_initialize", "eager_auth", "app_init", "session_create"];
        var phases = new List<string>();
        var log = Path.Combine(Root, "grok", "logs", "unified.jsonl");
        if (File.Exists(log))
        {
            using var stream = new FileStream(log, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            while (reader.ReadLine() is { } line)
            {
                JsonDocument entry;
                try
                {
                    entry = JsonDocument.Parse(line);
                }
                catch (JsonException)
                {
                    // A process terminated during a write may leave a partial final record.
                    continue;
                }
                using (entry)
                {
                    var root = entry.RootElement;
                    if (root.TryGetProperty("msg", out var message) &&
                        message.GetString() is "startup phase" or "startup phase running long" &&
                        root.TryGetProperty("ctx", out var context) &&
                        context.TryGetProperty("phase", out var value) &&
                        value.GetString() is { } phase && known.Contains(phase, StringComparer.Ordinal))
                    {
                        phases.Add(phase);
                    }
                }
            }
        }
        var diagnostics = Directory.CreateDirectory(Path.Combine(FindRepository(), "artifacts", "diagnostics")).FullName;
        File.WriteAllText(Path.Combine(diagnostics, Path.GetFileName(Root) + ".phases.json"), JsonSerializer.Serialize(phases, ManagementJsonContext.Default.ListString));
    }

    public async ValueTask DisposeAsync()
    {
        await _server.StopAsync();
        await _server.DisposeAsync();
        WriteStartupPhaseSummary();
        Directory.Delete(Root, recursive: true);
    }
}
