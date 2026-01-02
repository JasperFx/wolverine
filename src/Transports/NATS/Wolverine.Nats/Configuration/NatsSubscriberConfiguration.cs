using Wolverine.Configuration;
using Wolverine.Nats.Internal;

namespace Wolverine.Nats.Configuration;

/// <summary>
/// Configuration for NATS publishers/subscribers
/// </summary>
public class NatsSubscriberConfiguration
    : SubscriberConfiguration<NatsSubscriberConfiguration, NatsEndpoint>
{
    public NatsSubscriberConfiguration(NatsEndpoint endpoint)
        : base(endpoint) { }

    /// <summary>
    /// Use JetStream for durable message publishing
    /// </summary>
    public NatsSubscriberConfiguration UseJetStream(string? streamName = null)
    {
        add(endpoint =>
        {
            endpoint.UseJetStream = true;
            endpoint.StreamName = streamName ?? endpoint.Subject.Replace(".", "_").ToUpper();
        });

        return this;
    }
}
