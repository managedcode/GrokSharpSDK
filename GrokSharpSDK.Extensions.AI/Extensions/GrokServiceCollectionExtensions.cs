using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace ManagedCode.GrokSharpSDK.Extensions.AI.Extensions;

public static class GrokServiceCollectionExtensions
{
    public static IServiceCollection AddGrokChatClient(this IServiceCollection services,
        Action<GrokChatClientOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        var options = new GrokChatClientOptions();
        configure?.Invoke(options);
        services.AddSingleton<IChatClient>(_ => new GrokChatClient(options));
        return services;
    }

    public static IServiceCollection AddKeyedGrokChatClient(this IServiceCollection services,
        object serviceKey, Action<GrokChatClientOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(serviceKey);
        var options = new GrokChatClientOptions();
        configure?.Invoke(options);
        services.AddKeyedSingleton<IChatClient>(serviceKey, (_, _) => new GrokChatClient(options));
        return services;
    }
}
