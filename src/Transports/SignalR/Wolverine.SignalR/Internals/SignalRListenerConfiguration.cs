using Microsoft.AspNetCore.SignalR;
using System.Text.Json;
using Wolverine.Configuration;

namespace Wolverine.SignalR.Internals;

public class SignalRListenerConfiguration : ListenerConfiguration<SignalRListenerConfiguration, SignalRTransport>
{
    internal SignalRListenerConfiguration(SignalRTransport endpoint) : base(endpoint)
    {
    }

    /// <summary>
    /// Override the JSON serialization settings
    /// </summary>
    /// <param name="options"></param>
    /// <returns></returns>
    public SignalRListenerConfiguration OverrideJson(JsonSerializerOptions options)
    {
        add(e => e.JsonOptions = options);
        return this;
    }
}