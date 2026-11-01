using System.Diagnostics;
using System.Runtime.ExceptionServices;
using System.Text;
using ManagedCode.GrokSharpSDK.Configuration;
using ManagedCode.GrokSharpSDK.Models;

namespace ManagedCode.GrokSharpSDK.Internal;

internal static class CliMetadataProbe
{
    internal static string ReadVersion(GrokOptions options, TimeSpan timeout, int maximumCharacters)
    {
        // Upstream clap VERSION_WITH_COMMIT: grok <version> (<commit>).
        var parts = Read(options, ["--version"], timeout, maximumCharacters)
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2 || parts[0] != "grok" || !Version.TryParse(parts[1], out _))
        {
            throw new InvalidDataException("Grok returned an invalid native version.");
        }
        return parts[1];
    }

    internal static string Read(GrokOptions options, string[] arguments, TimeSpan timeout, int maximumCharacters)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(timeout, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumCharacters);
        var launch = options.GetCliLaunchCommand();
        var info = new ProcessStartInfo(launch.ExecutablePath)
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = options.WorkingDirectory ?? Environment.CurrentDirectory,
        };
        foreach (var argument in launch.PrefixArguments.Concat(arguments))
        {
            info.ArgumentList.Add(argument);
        }
        info.Environment.Clear();
        foreach (var (name, value) in options.CreateEnvironment())
        {
            info.Environment[name] = value;
        }
        using var process = new Process { StartInfo = info };
        if (!process.Start())
        {
            throw new InvalidOperationException("Could not start the Grok metadata command.");
        }
        process.StandardInput.Close();
        var stdout = ReadBoundedAsync(process.StandardOutput, maximumCharacters);
        var stderr = ReadBoundedAsync(process.StandardError, maximumCharacters);
        Exception? failure = null;
        string? output = null;
        try
        {
            if (!process.WaitForExit((int)Math.Min(timeout.TotalMilliseconds, int.MaxValue)))
            {
                throw new TimeoutException("Grok metadata command exceeded its deadline.");
            }
            if (!Task.WaitAll([stdout, stderr], timeout))
            {
                throw new TimeoutException("Grok metadata pipes exceeded their deadline.");
            }
            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException("Grok metadata command failed.");
            }
            output = stdout.Result;
        }
        catch (Exception exception)
        {
            failure = exception;
        }
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
            if (!process.WaitForExit((int)Math.Min(options.ProcessTerminationTimeout.TotalMilliseconds, int.MaxValue)) ||
                !Task.WhenAll(stdout, stderr).ContinueWith(static _ => { }, TaskScheduler.Default)
                    .Wait(options.ProcessTerminationTimeout))
            {
                throw new TimeoutException("Native process or pipes remain active.");
            }
        }
        catch (Exception exception)
        {
            throw new CliExecutionFailureException("Could not confirm Grok metadata cleanup.", false, exception);
        }
        if (failure is not null)
        {
            ExceptionDispatchInfo.Capture(failure).Throw();
        }
        return output!;
    }

    private static async Task<string> ReadBoundedAsync(StreamReader reader, int maximumCharacters)
    {
        var text = new StringBuilder();
        var buffer = new char[4096]; // StreamReader's standard-sized transfer buffer; not a product limit.
        var overflow = false;
        int read;
        while ((read = await reader.ReadAsync(buffer).ConfigureAwait(false)) != 0)
        {
            if (text.Length <= maximumCharacters - read)
            {
                text.Append(buffer, 0, read);
            }
            else
            {
                overflow = true;
            }
        }
        return overflow ? throw new InvalidDataException("Grok metadata exceeded the configured output budget.") : text.ToString();
    }
}
