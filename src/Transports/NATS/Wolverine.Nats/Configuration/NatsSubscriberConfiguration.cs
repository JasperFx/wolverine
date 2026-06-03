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

    /// <summary>
    /// Override the suffix used to derive the NATS JetStream scheduling subject for native scheduled sends
    /// (default <c>.scheduled</c>). The derived subject must stay covered by the destination's stream.
    /// </summary>
    public NatsSubscriberConfiguration UseScheduleSubjectSuffix(string suffix)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(suffix, nameof(suffix));

        add(endpoint => endpoint.ScheduleSubjectSuffix = suffix);
        return this;
    }
}
