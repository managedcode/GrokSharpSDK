using System.Runtime.CompilerServices;
using System.Text;
using ManagedCode.GrokSharpSDK.Configuration;
using ManagedCode.GrokSharpSDK.Internal;
using ManagedCode.GrokSharpSDK.Models;

namespace ManagedCode.GrokSharpSDK.Client;

public sealed partial class GrokClient(GrokOptions? options = null) : IDisposable
{
    private const string PromptPrefix = "grok-sdk-";
    private const string PromptSuffix = ".txt";
    private const string PromptFileFlag = "--prompt-file";
    private const string FormatFlag = "--output-format";
    private const string StreamingFormat = "streaming-json";
    private const string ModelFlag = "--model";
    private const string CwdFlag = "--cwd";
    private const string ResumeFlag = "--resume";
    private const string RulesFlag = "--rules";
    private const string EffortFlag = "--reasoning-effort";
    private const string SchemaFlag = "--json-schema";
    private const string NativeFailedMessage = "Grok Build CLI failed. Exit code: ";
    private const string MissingTerminalMessage = "Grok exited without a complete terminal result.";
    private const string InvalidSchemaMessage = "Grok did not return the requested structured output.";
    private readonly GrokOptions _options = options ?? new();
    private readonly CancellationTokenSource _lifetime = new();
    private int _disposed;

    public CliLaunchCommand GetCliLaunchCommand() => _options.GetCliLaunchCommand();

    public Task<GrokLoginSession> BeginLoginAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        return GrokLoginSession.StartAsync(_options, cancellationToken);
    }

    public async Task<GrokRunResult> RunAsync(string prompt, GrokRunOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var text = new StringBuilder();
        GrokEvent? terminal = null;
        await foreach (var update in RunStreamingAsync(prompt, options, cancellationToken).ConfigureAwait(false))
        {
            if (update.Type == NativeEventParser.TextEvent)
            {
                text.Append(update.Text);
            }
            else if (update.Type == NativeEventParser.EndEvent)
            {
                terminal = update;
            }
        }
        var result = terminal ?? throw new InvalidDataException(MissingTerminalMessage);
        return new GrokRunResult(text.ToString(), result.SessionId!, result.RequestId!,
            result.StopReason!, result.Usage, result.StructuredOutput, result.UsageIsIncomplete);
    }

    public async IAsyncEnumerable<GrokEvent> RunStreamingAsync(string prompt, GrokRunOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(prompt);
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        lifetime.Token.ThrowIfCancellationRequested();
        Directory.CreateDirectory(_options.TemporaryDirectory);
        var path = Path.Combine(_options.TemporaryDirectory, string.Concat(PromptPrefix, Guid.CreateVersion7(), PromptSuffix));
        var fileOptions = new FileStreamOptions
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.ReadWrite,
            Share = FileShare.Read | FileShare.Delete,
            Options = FileOptions.Asynchronous | FileOptions.DeleteOnClose,
        };
        if (!OperatingSystem.IsWindows())
        {
            fileOptions.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        }
        await using var file = new FileStream(path, fileOptions);
        await using (var writer = new StreamWriter(file, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), leaveOpen: true))
        {
            await writer.WriteAsync(prompt.AsMemory(), lifetime.Token).ConfigureAwait(false);
            await writer.FlushAsync(lifetime.Token).ConfigureAwait(false);
        }
        await foreach (var update in RunFileStreamingAsync(path, options, lifetime.Token).ConfigureAwait(false))
        {
            yield return update;
        }
    }

    public async IAsyncEnumerable<GrokEvent> RunFileStreamingAsync(string promptFile, GrokRunOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(promptFile);
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        lifetime.Token.ThrowIfCancellationRequested();
        options ??= new();
        var arguments = new List<string>(options.AdditionalArguments);
        Add(PromptFileFlag, Path.GetFullPath(promptFile));
        Add(FormatFlag, StreamingFormat);
        Add(ModelFlag, options.Model);
        Add(CwdFlag, options.WorkingDirectory);
        Add(ResumeFlag, options.ResumeSessionId);
        Add(RulesFlag, options.Instructions);
        Add(EffortFlag, options.ReasoningEffort);
        if (options.JsonSchema is { } schema)
        {
            Add(SchemaFlag, schema.GetRawText());
        }
        await using var process = new CliProcess(_options, arguments.ToArray());
        await using var updates = ReadEventsAsync(process, options, lifetime.Token).GetAsyncEnumerator(lifetime.Token);
        while (true)
        {
            bool available;
            try
            {
                available = await updates.MoveNextAsync().ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                // Join before reporting a recoverable provider failure. Cleanup failure overrides it.
                await process.DisposeAsync().ConfigureAwait(false);
                if (exception is OperationCanceledException)
                {
                    throw;
                }
                throw new CliExecutionFailureException("Grok Build CLI execution failed.", true, exception);
            }
            if (!available)
            {
                break;
            }
            yield return updates.Current;
        }

        void Add(string flag, string? value)
        {
            if (value is not null)
            {
                arguments.Add(flag);
                arguments.Add(value);
            }
        }
    }

    private static async IAsyncEnumerable<GrokEvent> ReadEventsAsync(CliProcess process, GrokRunOptions options,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        GrokEvent? terminal = null;
        await foreach (var output in process.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            if (output.StandardError || string.IsNullOrWhiteSpace(output.Line))
            {
                continue;
            }
            if (terminal is not null)
            {
                throw new InvalidDataException(MissingTerminalMessage);
            }
            var update = NativeEventParser.Parse(output.Line);
            if (update.Type == NativeEventParser.ErrorEvent)
            {
                throw new InvalidOperationException(NativeEventParser.GetString(update.Raw, "message") ?? NativeFailedMessage);
            }
            if (update.Type == NativeEventParser.EndEvent)
            {
                terminal = update;
            }
            else
            {
                yield return update;
            }
        }
        var exitCode = await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        if (exitCode != 0)
        {
            throw new InvalidOperationException(string.Concat(NativeFailedMessage,
                exitCode.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        }
        if (terminal is null || string.IsNullOrWhiteSpace(terminal.SessionId) ||
            string.IsNullOrWhiteSpace(terminal.RequestId) || string.IsNullOrWhiteSpace(terminal.StopReason))
        {
            throw new InvalidDataException(MissingTerminalMessage);
        }
        if (terminal.StopReason == NativeEventParser.CancelledReason)
        {
            throw new OperationCanceledException(cancellationToken);
        }
        if (options.JsonSchema is not null && (terminal.StructuredOutput is null || NativeEventParser.GetString(terminal.Raw, "structuredOutputError") is not null))
        {
            throw new InvalidDataException(NativeEventParser.GetString(terminal.Raw, "structuredOutputError") ?? InvalidSchemaMessage);
        }
        yield return terminal;

    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _lifetime.Cancel();
            _lifetime.Dispose();
        }
    }
}
