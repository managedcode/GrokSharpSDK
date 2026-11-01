using System.Runtime.CompilerServices;
using ManagedCode.GrokSharpSDK.Internal;
using ManagedCode.GrokSharpSDK.Models;

namespace ManagedCode.GrokSharpSDK.Client;

public sealed partial class GrokClient
{
    /// <summary>Reads native version and model catalog without an inference turn.</summary>
    public GrokCliMetadata GetCliMetadata(TimeSpan timeout, int maximumOutputCharacters)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        var version = CliMetadataProbe.ReadVersion(_options, timeout, maximumOutputCharacters);
        var catalog = CliMetadataProbe.Read(_options, ["models"], timeout, maximumOutputCharacters);
        const string defaultPrefix = "Default model: ";
        var models = new List<GrokCliModel>();
        string? defaultModel = null;
        var inCatalog = false;
        foreach (var line in catalog.Split('\n'))
        {
            var value = line.Trim();
            if (value.StartsWith(defaultPrefix, StringComparison.Ordinal))
            {
                defaultModel = value[defaultPrefix.Length..].Trim();
            }
            else if (value == "Available models:")
            {
                inCatalog = true;
            }
            else if (inCatalog && (value.StartsWith("* ", StringComparison.Ordinal) || value.StartsWith("- ", StringComparison.Ordinal)))
            {
                var model = value[2..];
                const string defaultSuffix = " (default)";
                if (model.EndsWith(defaultSuffix, StringComparison.Ordinal))
                {
                    model = model[..^defaultSuffix.Length];
                }
                if (!string.IsNullOrWhiteSpace(model) && !models.Any(item => item.Id == model))
                {
                    models.Add(new(model));
                }
            }
        }
        if (defaultModel is null || !models.Any(model => model.Id == defaultModel))
        {
            throw new InvalidDataException("Grok returned an incomplete native model catalog.");
        }
        return new(version, defaultModel, models.AsReadOnly());
    }

    public async IAsyncEnumerable<CliInstallationUpdate> InstallOrUpdateCliAsync(CliInstallationOptions options,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        await using var updates = CliInstallation.InstallAsync(options, lifetime.Token).GetAsyncEnumerator(lifetime.Token);
        while (true)
        {
            bool available;
            try
            {
                available = await updates.MoveNextAsync().ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                await updates.DisposeAsync().ConfigureAwait(false);
                if (exception is CliExecutionFailureException ||
                    exception is OperationCanceledException && lifetime.IsCancellationRequested)
                {
                    throw;
                }
                if (exception is OperationCanceledException)
                {
                    throw new CliExecutionFailureException("Grok CLI installation exceeded its deadline.", true,
                        new TimeoutException("The package-manager installation deadline expired.", exception));
                }
                throw new CliExecutionFailureException("Grok CLI installation failed.", true, exception);
            }
            if (!available)
            {
                break;
            }
            yield return updates.Current;
        }
    }
}
