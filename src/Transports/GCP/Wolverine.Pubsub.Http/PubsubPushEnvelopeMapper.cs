using System.Security.Cryptography;
using Google.Cloud.PubSub.V1;
using ImTools;
using JasperFx.Core;
using Microsoft.AspNetCore.Http;
using Wolverine.Http.Transport.Push;
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
///     with others cannot be received over push because the receiver takes one envelope per request, so send to a
///     topic with a push subscription inline (<c>SendInline()</c>), as every sender is under
///     <see cref="DurabilityMode.ServerlessWithOutbox" />
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

    public async ValueTask<Envelope?> MapAsync(HttpContext context, CancellationToken cancellation)
    {
        var push = await PubsubPushRequest.ReadAsync(context.Request.Body, cancellation);
        if (push == null) return null;

        var target = targetFor(push.Subscription);
        var message = push.Message;

        Envelope envelope;
        if (message.Attributes.ContainsKey("batched"))
        {
            var batched = EnvelopeSerializer.ReadMany(message.Data.ToByteArray());
            if (batched.Length != 1)
            {
                throw new NotSupportedException(
                    $"Pub/Sub message {message.MessageId} pushed from subscription '{push.Subscription}' is a batch of {batched.Length} Wolverine envelopes, but a push receiver takes one envelope per request. Send to this topic inline (SendInline()) so every message is published on its own");
            }

            envelope = batched[0];
        }
        else
        {
            envelope = new Envelope();
            var assigned = envelope.Id;

            var mapper = target.Endpoint.EnvelopeMapper ??= target.Endpoint.BuildMapper(_runtime);
            mapper.MapIncomingToEnvelope(envelope, message);

            // A message Wolverine did not publish carries no Wolverine id (the mapping leaves an empty one), so derive
            // a stable one from the Pub/Sub message id. A redelivery then has the same id, and the push receiver's
            // inbox deduplication sees it
            if ((envelope.Id == assigned || envelope.Id == Guid.Empty) && message.MessageId.IsNotEmpty())
            {
                envelope.Id = idFor(push.Subscription, message.MessageId);
            }
        }

        if (target.IsKnown) envelope.Destination ??= target.Endpoint.Uri;
        if (target.TenantId != null) envelope.TenantId ??= target.TenantId;

        return envelope;
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

    private static Guid idFor(string subscription, string messageId)
    {
        var hash = SHA256.HashData(System.Text.Encoding.UTF8.GetBytes($"pubsub:{subscription}:{messageId}"));
        return new Guid(hash.AsSpan(0, 16));
    }

    private record PushTarget(PubsubEndpoint Endpoint, string? TenantId, bool IsKnown);
}
