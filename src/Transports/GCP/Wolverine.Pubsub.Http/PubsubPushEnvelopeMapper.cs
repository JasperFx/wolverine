using Google.Cloud.PubSub.V1;
using ImTools;
using JasperFx.Core;
using Microsoft.AspNetCore.Http;
using Wolverine.Http.Transport.Push;
using Wolverine.Pubsub.Internal;
using Wolverine.Runtime;
using Wolverine.Runtime.Serialization;

namespace Wolverine.Pubsub.Http;

/// <summary>
///     GH-5016. Reads the request a Google Cloud Platform Pub/Sub push subscription sends into a Wolverine envelope for
///     the push receiver. The pushed message is mapped by the Pub/Sub endpoint whose subscription it came from, through
///     that endpoint's own <see cref="IPubsubEnvelopeMapper" />, so a custom mapper or interop configured with
///     <c>ListenToPubsubTopic()</c> applies to pushed messages exactly as it does to pulled ones. Use it through
///     <see cref="PubsubPushReceiverExtensions.MapPubsubPushReceiver" />, or
///     <c>MapWolverinePushReceiver&lt;PubsubPushEnvelopeMapper&gt;()</c>
/// </summary>
/// <remarks>
///     Expects the wrapped push format, the default for a push subscription. A message a Wolverine sender batched
///     with others is read into all of its envelopes, which the push receiver handles in order and acks together,
///     just as a pulled Pub/Sub listener in <see cref="Wolverine.Configuration.EndpointMode.NativeAck" /> mode settles one delivery.
///     When the push request carries a <c>deliveryAttempt</c>, which Pub/Sub only sends for a subscription with a dead
///     letter policy, every envelope starts from the attempts before it, so error policies that count attempts
///     escalate across redeliveries (GH-5048)
/// </remarks>
public class PubsubPushEnvelopeMapper : IPushEnvelopeMapper
{
    private const string FallbackEndpointName = "wolverine-push";

    private readonly IWolverineRuntime _runtime;
    private ImHashMap<string, PushTarget> _targets = ImHashMap<string, PushTarget>.Empty;

    public PubsubPushEnvelopeMapper(IWolverineRuntime runtime)
    {
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
    }

    /// <summary>
    ///     The Pub/Sub endpoint that maps every pushed message, like <c>pubsub://my-project/orders</c>. By default the
    ///     endpoint is the one whose subscription the push request names, and a subscription Wolverine does not know
    ///     is read by the default Pub/Sub mapping
    /// </summary>
    public Uri? EndpointUri { get; init; }

    public async ValueTask<IReadOnlyList<Envelope>> MapAsync(HttpContext context, CancellationToken cancellation)
    {
        var push = await PubsubPushRequest.ReadAsync(context.Request.Body, cancellation);
        if (push == null) return [];

        var target = targetFor(push.Subscription);
        var message = push.Message;

        Envelope[] envelopes;
        if (message.Attributes.ContainsKey("batched"))
        {
            // Wolverine's own batching sender, which marks every message it publishes as a batch, even a batch of one
            envelopes = EnvelopeSerializer.ReadMany(message.Data.ToByteArray());
        }
        else
        {
            var mapper = target.Endpoint.EnvelopeMapper ??= target.Endpoint.BuildMapper(_runtime);

            // A message Wolverine did not publish carries no Wolverine id, so it gets a stable one derived from the
            // Pub/Sub message id. A redelivery then has the same id, and the push receiver's inbox deduplication
            // sees it. A pulled delivery of the same message to the same subscription gets the same id
            var envelope = PubsubMessageIdentity.ReadEnvelope(mapper, message, push.Subscription);

            envelopes = [envelope];
        }

        foreach (var envelope in envelopes)
        {
            if (target.IsKnown) envelope.Destination ??= target.Endpoint.Uri;
            if (target.TenantId != null) envelope.TenantId ??= target.TenantId;

            // GH-5048. Each redelivery is mapped into a new envelope, so without the broker's count every one of them
            // would be a first attempt and the error policies that count attempts would never escalate. Handling
            // the message counts this attempt, so it starts from the attempts before it
            if (push.DeliveryAttempt is { } attempt) envelope.Attempts = attempt - 1;
        }

        return envelopes;
    }

    private PushTarget targetFor(string subscription)
    {
        if (_targets.TryFind(subscription, out var target)) return target;

        target = EndpointUri == null ? findBySubscription(subscription) : findByUri(subscription);
        _targets = _targets.AddOrUpdate(subscription, target);

        return target;
    }

    private PushTarget findByUri(string subscription)
    {
        var endpoint = _runtime.Options.Transports.TryGetEndpoint(EndpointUri!) as PubsubEndpoint
                       ?? throw new InvalidOperationException(
                           $"There is no Google Cloud Platform Pub/Sub endpoint configured at {EndpointUri}");

        return new PushTarget(endpoint, tenantFor(endpoint.Transport, projectOf(subscription)), true);
    }

    private PushTarget findBySubscription(string subscription)
    {
        var project = projectOf(subscription);
        var subscriptionId = subscription.Split('/').Last();
        var transports = _runtime.Options.Transports.OfType<PubsubTransport>().ToArray();

        foreach (var transport in transports)
        {
            if (project != null && project != transport.ProjectId &&
                transport.Tenants.All(x => x.ProjectId != project))
            {
                continue;
            }

            // Prefer the listening endpoint, as a topic endpoint that only publishes has a subscription name too
            var endpoint = transport.Topics.Concat(transport.Subscriptions)
                .Where(x => x.Server.Subscription.Name.SubscriptionId == subscriptionId)
                .OrderByDescending(x => x.IsListener)
                .FirstOrDefault();

            if (endpoint != null)
            {
                return new PushTarget(endpoint, tenantFor(transport, project), true);
            }
        }

        // An endpoint of its own, not registered with the transport, so the default Pub/Sub mapping reads it
        var fallback = new PubsubEndpoint(FallbackEndpointName, transports.FirstOrDefault() ?? new PubsubTransport());
        return new PushTarget(fallback, null, false);
    }

    private static string? tenantFor(PubsubTransport transport, string? project)
    {
        if (project == null || project == transport.ProjectId) return null;
        return transport.Tenants.FirstOrDefault(x => x.ProjectId == project)?.TenantId;
    }

    // projects/{project}/subscriptions/{subscription}
    private static string? projectOf(string subscription)
    {
        var parts = subscription.Split('/');
        return parts.Length == 4 && parts[0] == "projects" ? parts[1] : null;
    }

    private record PushTarget(PubsubEndpoint Endpoint, string? TenantId, bool IsKnown);
}
