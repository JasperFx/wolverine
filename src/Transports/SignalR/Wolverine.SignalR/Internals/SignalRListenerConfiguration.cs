using System.Text.Json;
using Wolverine.Configuration;

namespace Wolverine.SignalR.Internals;

public class SignalRListenerConfiguration : ListenerConfiguration<SignalRListenerConfiguration, SignalREndpoint>
{
    internal SignalRListenerConfiguration(SignalREndpoint endpoint) : base(endpoint)
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