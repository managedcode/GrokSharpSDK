using ManagedCode.GrokSharpSDK.Configuration;
using ManagedCode.GrokSharpSDK.Models;

namespace ManagedCode.GrokSharpSDK.Extensions.AI;

public sealed class GrokChatClientOptions
{
    public GrokOptions ClientOptions { get; set; } = new();
    public GrokRunOptions RunOptions { get; set; } = new();
}
