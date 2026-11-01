using System.Text.RegularExpressions;
using ManagedCode.GrokSharpSDK.Configuration;
using ManagedCode.GrokSharpSDK.Internal;
using ManagedCode.GrokSharpSDK.Models;

namespace ManagedCode.GrokSharpSDK.Client;

public sealed partial class GrokLoginSession : IAsyncDisposable
{
    private const string LoginCommand = "login";
    private const string DeviceFlag = "--device-auth";
    private const string ConfirmPrompt = "Confirm this code in your browser:";
    private const string EnterPrompt = "Then enter this code:";
    private const string FailedMessage = "Grok native sign-in did not complete successfully.";
    private const string UrlPattern = @"https://[^\s\x1b\x07]+";
    private const string CodePattern = @"^[A-Za-z0-9-]+$";
    private readonly CliProcess _process;
    private readonly CancellationTokenSource _lifetime;
    private readonly TaskCompletionSource<CliLoginInstructions> _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Task _completion;
    private int _disposed;
    private int _cancelRequested;

    private GrokLoginSession(GrokOptions options, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        _lifetime = CancellationTokenSource.CreateLinkedTokenSource(token);
        try
        {
            _process = new CliProcess(options, [LoginCommand, DeviceFlag]);
        }
        catch
        {
            _lifetime.Dispose();
            throw;
        }
        _completion = RunAsync();
    }

    public CliLoginInstructions Instructions { get; private set; } = null!;

    internal static async Task<GrokLoginSession> StartAsync(GrokOptions options, CancellationToken token)
    {
        var session = new GrokLoginSession(options, token);
        try
        {
            await session._ready.Task.ConfigureAwait(false);
            return session;
        }
        catch
        {
            await session.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public Task WaitForCompletionAsync(CancellationToken cancellationToken = default) => _completion.WaitAsync(cancellationToken);

    public async Task CancelAsync(CancellationToken cancellationToken = default)
    {
        if (!_completion.IsCompleted && Interlocked.Exchange(ref _cancelRequested, 1) == 0)
        {
            await _lifetime.CancelAsync().ConfigureAwait(false);
        }
        await _completion.WaitAsync(cancellationToken).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        cancellationToken.ThrowIfCancellationRequested();
        await _process.DisposeAsync().ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }
        try
        {
            await CancelAsync().ConfigureAwait(false);
            await _process.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            _lifetime.Dispose();
        }
    }

    private async Task RunAsync()
    {
        Uri? url = null;
        var expectingCode = false;
        try
        {
            await foreach (var output in _process.ReadAsync(_lifetime.Token).ConfigureAwait(false))
            {
                var line = output.Line.Trim();
                var match = SignInUrl().Match(line);
                if (match.Success && Uri.TryCreate(match.Value, UriKind.Absolute, out var parsed))
                {
                    url = parsed;
                }
                if (line is ConfirmPrompt or EnterPrompt)
                {
                    expectingCode = true;
                }
                else if (expectingCode && DeviceCode().IsMatch(line) && url is not null)
                {
                    Instructions = new CliLoginInstructions(url, line);
                    _ready.TrySetResult(Instructions);
                    expectingCode = false;
                }
            }
            if (await _process.WaitForExitAsync(_lifetime.Token).ConfigureAwait(false) != 0 || !_ready.Task.IsCompletedSuccessfully)
            {
                throw new InvalidOperationException(FailedMessage);
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
            _ready.TrySetCanceled(_lifetime.Token);
            throw;
        }
        catch (Exception exception)
        {
            _ready.TrySetException(exception);
            throw;
        }
        finally
        {
            await _process.DisposeAsync().ConfigureAwait(false);
        }
    }

    [GeneratedRegex(UrlPattern, RegexOptions.CultureInvariant)]
    private static partial Regex SignInUrl();
    [GeneratedRegex(CodePattern, RegexOptions.CultureInvariant)]
    private static partial Regex DeviceCode();
}
