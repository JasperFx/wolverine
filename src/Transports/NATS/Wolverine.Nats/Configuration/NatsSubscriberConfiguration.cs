using System.Text.Json;
using Wolverine.Configuration;
using Wolverine.Nats.Internal;
using Wolverine.Runtime.Serialization;

namespace Wolverine.Nats.Configuration;

/// <summary>
/// Configuration for NATS publishers/subscribers
/// </summary>
public class NatsSubscriberConfiguration
    : InteroperableSubscriberConfiguration<NatsSubscriberConfiguration, NatsEndpoint, INatsEnvelopeMapper, NatsEnvelopeMapper>
{
    public NatsSubscriberConfiguration(NatsEndpoint endpoint)
        : base(endpoint) { }

    /// <summary>
    /// Publish only the serialized JSON of each message to this subject, with none of Wolverine's headers, for
    /// subscribers that are not Wolverine. Headers added with <see cref="AddOutgoingHeader"/> are still written.
    /// Request/reply and the tenant-id header need Wolverine's headers, so they do not work through such an endpoint.
    /// </summary>
    /// <param name="options">
    /// When supplied, becomes this endpoint's default JSON serialization settings for outgoing message bodies.
    /// When omitted, Wolverine's default System.Text.Json settings apply.
    /// </param>
    /// <returns></returns>
    public NatsSubscriberConfiguration PublishRawJson(JsonSerializerOptions? options = null)
    {
        if (options != null)
        {
            add(e => e.DefaultSerializer = new SystemTextJsonSerializer(options));
        }

        // The parameter order matters: (e, _) would bind to the Action<TEndpoint, TConcreteMapper> customization
        // overload of UseInterop and leave the built in mapper in place. See GH-3407.
        return UseInterop((_, e) => new JsonOnlyMapper(e));
    }

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
        endpoint.StreamName = streamName ?? endpoint.Subject.Replace(".", "_").ToUpperInvariant();
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
