using System.Text.Json;
using Wolverine.Configuration;

namespace Wolverine.SignalR.Internals;

public class SignalRSubscriberConfiguration : SubscriberConfiguration<SignalRSubscriberConfiguration, SignalRTransport>
{
    internal SignalRSubscriberConfiguration(SignalRTransport endpoint) : base(endpoint)
    {
    }
    
    /// <summary>
    /// Override the JSON serialization settings
    /// </summary>
    /// <param name="options"></param>
    /// <returns></returns>
    public SignalRSubscriberConfiguration OverrideJson(JsonSerializerOptions options)
    {
        add(e => e.JsonOptions = options);
        return this;
    }
}