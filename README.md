# ManagedCode.GrokSharpSDK

.NET 10 SDK for the official [Grok Build coding agent](https://github.com/xai-org/grok-build), with a Microsoft.Extensions.AI `IChatClient` adapter.

The SDK runs the installed Grok CLI. Grok owns agent tools, provider requests, sign-in credentials and persisted conversations. Compatibility target: `@xai-official/grok@1.0.70`. This is a ManagedCode project, independent of xAI.

## Install

Install the native CLI first:

```sh
npm install -g @xai-official/grok@1.0.70
grok --version
grok login --device-auth
```

Package IDs:

- `ManagedCode.GrokSharpSDK` — process execution, native events, sessions and sign-in.
- `ManagedCode.GrokSharpSDK.Extensions.AI` — optional MEAI adapter and DI.

Both libraries target `net10.0`. A package is available only after a successful NuGet publication; check the repository's release status before installing a new version. The native CLI is a separate prerequisite.

## Run and resume

```csharp
using ManagedCode.GrokSharpSDK.Client;
using ManagedCode.GrokSharpSDK.Configuration;
using ManagedCode.GrokSharpSDK.Models;

using var client = new GrokClient(new GrokOptions
{
    // Optional when grok is already on PATH.
    ExecutablePath = "/absolute/path/to/grok",
});
var options = new GrokRunOptions { WorkingDirectory = "/path/to/repository" };
var first = await client.RunAsync("Explain this repository.", options, cancellationToken);
var next = await client.RunAsync("Where should I add a test?",
    options with { ResumeSessionId = first.SessionId }, cancellationToken);
Console.WriteLine(next.Text);
```

Omit `Model` to use the native CLI's configured default. Configure custom providers in Grok's own config, or set `Model` to a model ID that your installed CLI supports. No model API key is configured by this SDK.

Use `RunStreamingAsync` for native `streaming-json` events. Text events contain real deltas. Tool activity and the original JSON are available through `GrokEvent.Raw`. An `end` event is delivered only after a complete native result and exit code zero. `RunFileStreamingAsync` accepts a native prompt file, including the CLI's JSON content-block files.

## Obtain a sign-in URL on a server

```csharp
await using var login = await client.BeginLoginAsync(cancellationToken);

// Send both values to the browser through your application's authenticated channel.
Uri url = login.Instructions.LoginUrl;
string code = login.Instructions.UserCode;

// Keep the session alive while the user approves in their browser.
await login.WaitForCompletionAsync(cancellationToken);
```

The CLI performs device authorization and stores credentials in its native profile. `BeginLoginAsync` starts a real operation; the URL is not a static portal link. Keep the returned session alive on the server until sign-in completes or the user cancels it. Configure `GROK_HOME` through `GrokOptions.EnvironmentVariables` for an isolated profile.

Cancelling a token passed to `WaitForCompletionAsync` stops that wait only. `CancelAsync`, disposal, or cancellation of the token passed to `BeginLoginAsync` stops the sign-in process. No authorization code is submitted through SDK stdin for this device flow. Browser approval remains a user action.

## Microsoft.Extensions.AI

```csharp
using ManagedCode.GrokSharpSDK.Extensions.AI.Extensions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

var services = new ServiceCollection();
services.AddGrokChatClient(options =>
{
    options.RunOptions = new() { WorkingDirectory = "/path/to/repository" };
});
using var provider = services.BuildServiceProvider();
var chat = provider.GetRequiredService<IChatClient>();
var response = await chat.GetResponseAsync(
    [new ChatMessage(ChatRole.User, "Explain this repository.")],
    cancellationToken: cancellationToken);

var followup = await chat.GetResponseAsync(
    [new ChatMessage(ChatRole.User, "Suggest a focused test.")],
    new ChatOptions { ConversationId = response.ConversationId },
    cancellationToken);
```

`AddKeyedGrokChatClient("grok", configure)` supports keyed DI. `GetStreamingResponseAsync` forwards text deltas, native activity in `RawRepresentation` and final usage/session metadata.

Supported mapping:

| MEAI input | Native behavior |
| --- | --- |
| User/assistant text history | Role-labelled prompt for a fresh session |
| System/developer text and `Instructions` | `--rules` |
| `ModelId` | `--model` |
| `ConversationId` | `--resume`; send turns after the last assistant message only |
| `ChatResponseFormatJson` with schema | `--json-schema` and validated native structured output |
| `RawRepresentationFactory` returning `GrokRunOptions` | Native per-call options |

When resuming, Grok's saved history is authoritative. Pass the new user turn, or conventional complete history ending with the new user turn; the adapter avoids replaying turns before the last assistant. For a fresh session, role labels preserve text history but do not create native protocol messages. System/developer text is appended to Grok's agent instructions; it does not replace its system prompt.

Images, MEAI function tools, generation sampling/token limits, tool modes, background operations, unspecified JSON mode and unknown additional properties are rejected. Native tools execute inside Grok, so they are not emitted as MEAI function calls for middleware to execute again. Configure native capabilities using the installed CLI's settings or explicit `GrokRunOptions`. Arguments are passed literally without a shell.

Usage includes uncached and cached input tokens. `UsageIsIncomplete` is preserved in the raw result/event. For structured streaming, validated JSON is emitted once at completion; ordinary assistant text is not presented as validated JSON deltas.

## Configuration and lifecycle

`GrokOptions` supports executable selection, an explicit launch command, environment overrides, environment inheritance, prompt-file location and a process cleanup deadline. Windows npm wrappers are resolved to the official package's Node entry point without invoking a shell. Configure a native executable directly when available.

Prompts are written to a private delete-on-close file and passed through `--prompt-file`. Streams drain stdout and stderr concurrently with backpressure. Cancellation, early stream disposal and client disposal cancel execution and terminate the owned process tree. Await the operation or dispose its enumerator to observe cleanup completion. Stderr is drained but is not logged or attached to generic process errors because it can contain sensitive data.

Native session/provider failures remain errors; there is no provider fallback or duplicate SDK conversation store. Raw events may contain prompts or file content: handle them as application data.

## Development

See [architecture](docs/Architecture/Overview.md), [execution and sign-in](docs/Features/Execution.md), [testing](docs/Testing.md), and [the design decision](docs/ADR/0001-native-cli.md).

The Apache-2.0 upstream submodule is pinned reference material, not a runtime component. SDK code is MIT licensed.
