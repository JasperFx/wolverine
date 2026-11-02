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
        // Right away as well, not only with the rest of the endpoint's configuration: endpoint policies such as
        // UseDurableOutboxOnAllSendingEndpoints() run first and ask whether the endpoint supports durable mode,
        // which a NATS endpoint only does with JetStream
        if (_endpoint != null)
        {
            applyJetStream(_endpoint, streamName);
        }

        add(endpoint => applyJetStream(endpoint, streamName));

        return this;
    }

    private static void applyJetStream(NatsEndpoint endpoint, string? streamName)
    {
        endpoint.UseJetStream = true;
        endpoint.StreamName = streamName ?? endpoint.Subject.Replace(".", "_").ToUpper();
    }

    /// <summary>
    /// GH-5342. Mark this endpoint's JetStream stream as owned by another system. Wolverine will not create it in
    /// <c>resources setup</c>, or touch it in <c>resources teardown</c>. <c>resources check</c> still checks that
    /// it exists. Do not also declare the stream with <c>DefineStream()</c>: a declared stream is one the
    /// application owns, and Wolverine provisions it.
    /// </summary>
    public NatsSubscriberConfiguration ExternallyOwned()
    {
        add(e => e.IsExternallyOwned = true);
        return this;
    }

    /// <summary>
    /// Override the suffix used to derive the NATS JetStream scheduling subjects for native scheduled sends
    /// (default <c>.scheduled</c>): each scheduled message is published to <c>{subject}{suffix}.{envelope id}</c>,
    /// which the destination's stream must cover, e.g. with <c>{subject}{suffix}.&gt;</c>.
    /// </summary>
    public NatsSubscriberConfiguration UseScheduleSubjectSuffix(string suffix)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(suffix, nameof(suffix));

        add(endpoint => endpoint.ScheduleSubjectSuffix = suffix);
        return this;
    }

    /// <summary>
    /// Add a static header written to every message published to this subject.
    /// </summary>
    public NatsSubscriberConfiguration AddOutgoingHeader(string key, string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key, nameof(key));

        add(endpoint => endpoint.CustomHeaders[key] = value);
        return this;
    }
}
