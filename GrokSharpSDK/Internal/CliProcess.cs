using System.Diagnostics;
using System.Threading.Channels;
using ManagedCode.GrokSharpSDK.Configuration;
using ManagedCode.GrokSharpSDK.Models;

namespace ManagedCode.GrokSharpSDK.Internal;

internal sealed class CliProcess : IAsyncDisposable
{
    private const string StartFailedMessage = "Could not start the configured Grok Build CLI.";
    private const string CleanupFailedMessage = "Could not confirm Grok CLI process and pipe cleanup within ProcessTerminationTimeout.";
    private readonly Process _process;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Channel<(bool StandardError, string Line)> _lines = Channel.CreateBounded<(bool, string)>(
        new BoundedChannelOptions(1) { SingleReader = true, SingleWriter = false });
    private readonly Task _stdout;
    private readonly Task _stderr;
    private readonly TimeSpan _terminationTimeout;
    private int _remaining = 2;
    private readonly TaskCompletionSource _disposeCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _disposeStarted;

    internal CliProcess(GrokOptions options, string[] arguments)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(options.ProcessTerminationTimeout, TimeSpan.Zero);
        var launch = options.GetCliLaunchCommand();
        var info = new ProcessStartInfo(launch.ExecutablePath)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = options.WorkingDirectory ?? Environment.CurrentDirectory,
        };
        foreach (var argument in launch.PrefixArguments.Concat(arguments))
        {
            info.ArgumentList.Add(argument);
        }
        info.Environment.Clear();
        foreach (var (key, value) in options.CreateEnvironment())
        {
            info.Environment[key] = value;
        }
        _terminationTimeout = options.ProcessTerminationTimeout;
        _process = new Process { StartInfo = info };
        var started = false;
        try
        {
            started = _process.Start();
            if (!started)
            {
                throw new InvalidOperationException(StartFailedMessage);
            }
            _process.StandardInput.Close();
        }
        catch (Exception exception)
        {
            try
            {
                if (started && !_process.HasExited)
                {
                    _process.Kill(entireProcessTree: true);
                    _process.WaitForExit((int)Math.Min(_terminationTimeout.TotalMilliseconds, int.MaxValue));
                }
            }
            finally
            {
                _process.Dispose();
                _shutdown.Dispose();
            }
            // Before pipe pumps exist, a post-start failure cannot prove complete child/pipe cleanup.
            throw new CliExecutionFailureException(StartFailedMessage, !started, exception);
        }
        _stdout = PumpAsync(_process.StandardOutput, false);
        _stderr = PumpAsync(_process.StandardError, true);
    }

    internal int Id => _process.Id;

    internal IAsyncEnumerable<(bool StandardError, string Line)> ReadAsync(CancellationToken token) =>
        _lines.Reader.ReadAllAsync(token);

    internal async Task<int> WaitForExitAsync(CancellationToken cancellationToken)
    {
        await _process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        return _process.ExitCode;
    }

    private async Task PumpAsync(StreamReader reader, bool standardError)
    {
        try
        {
            while (await reader.ReadLineAsync(_shutdown.Token).ConfigureAwait(false) is { } line)
            {
                await _lines.Writer.WriteAsync((standardError, line), _shutdown.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
        {
            // Expected when the operation stops consuming output or cancels.
        }
        catch (Exception exception)
        {
            _lines.Writer.TryComplete(exception);
            throw;
        }
        finally
        {
            if (Interlocked.Decrement(ref _remaining) == 0)
            {
                _lines.Writer.TryComplete();
            }
        }
    }

    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposeStarted, 1) == 0)
        {
            _ = CompleteDisposeAsync();
        }
        return new ValueTask(_disposeCompletion.Task);
    }

    private async Task CompleteDisposeAsync()
    {
        try
        {
            await DisposeCoreAsync().ConfigureAwait(false);
            _disposeCompletion.TrySetResult();
        }
        catch (Exception exception)
        {
            _disposeCompletion.TrySetException(exception);
        }
    }

    private async Task ObserveOutputCompletionAsync(Task drain)
    {
        // Read failures belong to the operation. Teardown must still await every pipe.
        await Task.WhenAll(_stdout, _stderr, drain).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
    }

    private async Task DrainRemainingOutputAsync()
    {
        await foreach (var _ in _lines.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            // The operation has stopped observing; drain to let native child pipes reach EOF.
        }
    }

    private async Task DisposeCoreAsync()
    {
        var outputCompletion = ObserveOutputCompletionAsync(DrainRemainingOutputAsync());
        try
        {
            if (!_process.HasExited)
            {
                try
                {
                    _process.Kill(entireProcessTree: true);
                }
                catch (InvalidOperationException) when (_process.HasExited)
                {
                    // Natural exit raced the cancellation.
                }
            }
            await _process.WaitForExitAsync().WaitAsync(_terminationTimeout).ConfigureAwait(false);
            await outputCompletion.WaitAsync(_terminationTimeout).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            throw new CliExecutionFailureException(CleanupFailedMessage, false, exception);
        }
        finally
        {
            await _shutdown.CancelAsync().ConfigureAwait(false);
            _process.StandardOutput.Dispose();
            _process.StandardError.Dispose();
            _process.Dispose();
            _shutdown.Dispose();
        }
    }
}
