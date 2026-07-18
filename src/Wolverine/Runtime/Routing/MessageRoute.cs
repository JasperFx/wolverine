using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using ImTools;
using JasperFx.Core;
using JasperFx.Core.Reflection;
using Microsoft.Extensions.Logging;
using Wolverine.Attributes;
using Wolverine.Configuration;
using Wolverine.Runtime.Partitioning;
using Wolverine.Runtime.RemoteInvocation;
using Wolverine.Runtime.Scheduled;
using Wolverine.Runtime.Serialization;
using Wolverine.Transports;
using Wolverine.Transports.Local;
using Wolverine.Transports.Sending;
using Wolverine.Util;

namespace Wolverine.Runtime.Routing;

public class MessageRoute : IMessageRoute, IMessageInvoker
{
    private static ImHashMap<Type, IList<IEnvelopeRule>> _rulesByMessageType =
        ImHashMap<Type, IList<IEnvelopeRule>>.Empty;

    private readonly IReplyTracker _replyTracker;
    private readonly MessagePartitioningRules _partitioning;
    private protected readonly Endpoint _endpoint;

    /// <summary>
    /// GH-2966: construct the right MessageRoute shape for an endpoint. Transports that carry the reply
    /// in the same request/response exchange (HTTP) get <see cref="InlineReplyMessageRoute"/>, which reads
    /// the reply straight off the protocol response instead of the reply-tracker/listener round trip.
    /// The shape is chosen once here at routing-compile time, so there is no per-message branching.
    /// </summary>
    public static MessageRoute For(Type messageType, Endpoint endpoint, IWolverineRuntime runtime)
    {
        return endpoint is IInlineRequestReplyEndpoint
            ? new InlineReplyMessageRoute(messageType, endpoint, runtime)
            : new MessageRoute(messageType, endpoint, runtime);
    }

    // CloseAndBuildAs<IMessageSerializer> on typeof(IntrinsicSerializer<>) closes
    // over the runtime-resolved message type when the type implements
    // ISerializable. Same reflective shape as the IntrinsicSerializer.Write path
    // suppressed in chunk D (#2756) — apps in TypeLoadMode.Static pre-discover
    // their ISerializable message types into a registered IMessageSerializer
    // cache at bootstrap, so this constructor's miss path never fires at steady
    // state. The AOT publishing guide documents the migration story.
    [UnconditionalSuppressMessage("Trimming", "IL2026",
        Justification = "Closed generic resolved from runtime messageType; AOT consumers pre-register ISerializable serializers. See AOT guide.")]
    [UnconditionalSuppressMessage("AOT", "IL3050",
        Justification = "Closed generic resolved from runtime messageType; AOT consumers pre-register ISerializable serializers. See AOT guide.")]
    public MessageRoute(Type messageType, Endpoint endpoint, IWolverineRuntime runtime)
    {
        IsLocal = endpoint is LocalQueue;
        _replyTracker = runtime.Replies;
        _partitioning = runtime.Options.MessagePartitioning;

        if (WolverineSystemPart.WithinDescription)
        {
            Sender = endpoint.Agent!;
        }
        else
        {
            // Might need to force it to build out the sending agent if this is the first time it's been used
            Sender = endpoint.Agent ?? runtime.Endpoints.GetOrBuildSendingAgent(endpoint.Uri) ?? throw new ArgumentOutOfRangeException(nameof(endpoint), $"Endpoint {endpoint.Uri} does not have an active sending agent. Message type: {messageType.FullNameInCode()}");
        }

        IsLocal = endpoint is LocalQueue;

        if (messageType.CanBeCastTo(typeof(ISerializable)))
        {
            Serializer = typeof(IntrinsicSerializer<>).CloseAndBuildAs<IMessageSerializer>(messageType);
        }
        else if (WolverineSystemPart.WithinDescription)
        {
            Serializer = endpoint.DefaultSerializer!;
        }
        else
        {
            Serializer = endpoint.DefaultSerializer ?? throw new ArgumentOutOfRangeException(nameof(endpoint), "No DefaultSerializer on endpoint " + endpoint.Uri + ", Message type: " + messageType.FullNameInCode());
        }

        Rules.AddRange(endpoint.OutgoingRules);
        Rules.AddRange(RulesForMessageType(messageType));

        MessageType = messageType;

        _endpoint = endpoint;
    }

    public Uri Uri => _endpoint.Uri;

    public Type MessageType { get; }

    public bool IsLocal { get; }

    public IMessageSerializer Serializer { get; } = null!;
    public ISendingAgent Sender { get; } = null!;

    public IList<IEnvelopeRule> Rules { get; } = new List<IEnvelopeRule>();

    public Task InvokeAsync(object message, MessageBus bus, CancellationToken cancellation = default,
        TimeSpan? timeout = null, DeliveryOptions? options = null)
    {
        return InvokeAsync<Acknowledgement>(message, bus, cancellation, timeout, options);
    }

    public IAsyncEnumerable<T> StreamAsync<T>(object message, MessageBus bus,
        CancellationToken cancellation = default,
        DeliveryOptions? options = null)
    {
        throw new NotSupportedException(
            $"StreamAsync is only supported for locally-handled messages. " +
            $"The message type '{message.GetType().FullNameInCode()}' is routed to a remote endpoint ({_endpoint.Uri}). " +
            $"Configure a local handler or use InvokeAsync<T> for remote request/reply.");
    }

    public Envelope CreateForSending(object message, DeliveryOptions? options, ISendingAgent localDurableQueue,
        WolverineRuntime runtime, string? topicName)
    {
        // GH-2897 defense-in-depth: a route can carry a null Sender if it was built during
        // description mode (WithinDescription) before the endpoint's sending agent existed.
        // Such routes are no longer cached, so this should never trip — but resolve the live
        // agent on demand rather than NRE inside the Envelope ctor (which dereferences
        // agent.Endpoint) if one ever reaches the send path.
        var sender = Sender ?? runtime.Endpoints.GetOrBuildSendingAgent(_endpoint.Uri)
            ?? throw new InvalidOperationException(
                $"Endpoint {_endpoint.Uri} does not have an active sending agent. Message type: {MessageType.FullNameInCode()}");

        // Pool the outgoing envelope when the sender's lifecycle is bounded —
        // i.e. inline-send agents that release the reference after the
        // synchronous send call returns. See wolverine#2955; AcquireOutgoingEnvelope
        // is a no-op (returns `new Envelope()`) for any other agent shape, so the
        // semantics here are unchanged for buffered / durable / local-queue routes.
        var envelope = runtime.AcquireOutgoingEnvelope(sender);
        envelope.Message = message;
        envelope.Sender = sender;
        envelope.Serializer = Serializer
            ?? (message is ISerializable ? IntrinsicSerializer.Instance : sender.Endpoint.DefaultSerializer);
        envelope.ContentType = envelope.Serializer?.ContentType;
        envelope.Destination = sender.Destination;
        envelope.ReplyUri = sender.ReplyUri?.MaybeCorrectScheme(sender.Destination.Scheme);
        envelope.TopicName = topicName;
        envelope.WireTap = _endpoint.WireTap;
        envelope.TenantId = options?.TenantId;
        envelope.GroupId = options?.GroupId;

        if (sender.Endpoint is LocalQueue)
        {
            envelope.Status = EnvelopeStatus.Incoming;
        }

        if (options != null && options.ContentType!.IsNotEmpty() && options.ContentType! != Serializer!.ContentType)
        {
            envelope.Serializer = runtime.Options.FindSerializer(options.ContentType);
            envelope.ContentType = envelope.Serializer.ContentType;
        }

        // Apply application wide message grouping policies after stamping any
        // explicit TenantId/GroupId from DeliveryOptions so partitioning can
        // make decisions on the final outgoing envelope metadata.
        envelope.GroupId = _partitioning.DetermineGroupId(envelope);

        foreach (var rule in Rules) rule.Modify(envelope);

        // Delivery options win
        options?.Override(envelope);

        // Will need the topic persisted, see https://github.com/JasperFx/wolverine/issues/1100
        if (sender.Endpoint.RoutingType == RoutingMode.ByTopic && envelope.TopicName.IsEmpty())
        {
            envelope.TopicName = TopicRouting.DetermineTopicName(envelope);
        }

        var utcNow = DateTimeOffset.UtcNow;
        if (envelope.IsScheduledForLater(utcNow))
        {
            if (IsLocal)
            {
                envelope.Status = EnvelopeStatus.Scheduled;
                envelope.OwnerId = TransportConstants.AnyNode;
                runtime.Logger.LogDebug("Envelope {EnvelopeId} ({MessageType}) marked as Scheduled for local execution at {Destination}", envelope.Id, envelope.MessageType, envelope.Destination);
            }
            else if (!sender.SupportsNativeScheduledSendFor(envelope, utcNow))
            {
                runtime.Logger.LogDebug("Envelope {EnvelopeId} ({MessageType}) wrapped for durable scheduled send to {Destination} (transport does not support native scheduling for this envelope)", envelope.Id, envelope.MessageType, envelope.Destination);
                return envelope.ForScheduledSend(localDurableQueue);
            }
            else
            {
                runtime.Logger.LogDebug("Envelope {EnvelopeId} ({MessageType}) scheduled via native transport scheduling to {Destination}", envelope.Id, envelope.MessageType, envelope.Destination);
            }
        }
        else
        {
            envelope.OwnerId = runtime.Options.Durability.AssignedNodeNumber;
        }

        return envelope;
    }

    public MessageSubscriptionDescriptor Describe()
    {
        return new MessageSubscriptionDescriptor
        {
            ContentType = Serializer.ContentType,
            Endpoint = _endpoint.Uri
        };
    }
        
    public Task<T> InvokeAsync<T>(object message, MessageBus bus,
        CancellationToken cancellation = default,
        TimeSpan? timeout = null, DeliveryOptions? options = null)
    {
        return RemoteInvokeAsync<T>(message, bus, cancellation, timeout, options);
    }

    internal async Task<T> RemoteInvokeAsync<T>(object message, MessageBus bus, CancellationToken cancellation,
        TimeSpan? timeout, DeliveryOptions? options, string? topicName = null)
    {
        if (message == null)
        {
            throw new ArgumentNullException(nameof(message));
        }

        if (!bus.Runtime.Options.EnableRemoteInvocation)
        {
            throw new InvalidOperationException(
                $"Remote invocation is disabled in this application through the {nameof(WolverineOptions)}.{nameof(WolverineOptions.EnableRemoteInvocation)} value. Cannot invoke at requested endpoint {_endpoint.Uri}");
        }
        
        bus.Runtime.RegisterMessageType(typeof(T));

        timeout ??= bus.Runtime.Options.DefaultRemoteInvocationTimeout;

        var envelope = new Envelope(message, Sender)
        {
            TenantId = options?.TenantId ?? bus.TenantId,
            TopicName = topicName,
            WireTap = _endpoint.WireTap
        };
        
        options?.Override(envelope);

        foreach (var rule in Rules) rule.Modify(envelope);
        if (typeof(T) == typeof(Acknowledgement))
        {
            envelope.AckRequested = true;
        }
        else
        {
            envelope.ReplyRequested = typeof(T).ToMessageTypeName();
        }

        envelope.DeliverWithin = timeout.Value;
        envelope.Sender = Sender;

        @bus.TrackEnvelopeCorrelation(envelope, Activity.Current);
        
        // The request/reply envelope *must* use the envelope id for the conversation id
        // for proper tracking. See https://github.com/JasperFx/wolverine/issues/1176
        envelope.ConversationId = envelope.Id;

        return await sendAndAwaitReplyAsync<T>(envelope, bus, cancellation, timeout.Value).ConfigureAwait(false);
    }

    /// <summary>
    /// Send the request envelope and resolve the reply. The default (brokered) shape registers a
    /// <c>ReplyListener&lt;T&gt;</c> with the reply tracker and awaits the correlated reply envelope on the
    /// sender's listener loop. <see cref="InlineReplyMessageRoute"/> overrides this for transports whose
    /// protocol carries the reply in the same exchange (HTTP) — see GH-2966.
    /// </summary>
    protected virtual async Task<T> sendAndAwaitReplyAsync<T>(Envelope envelope, MessageBus bus,
        CancellationToken cancellation, TimeSpan timeout)
    {
        var waiter = _replyTracker.RegisterListener<T>(envelope, cancellation, timeout);

        await Sender.EnqueueOutgoingAsync(envelope);

        return await waiter;
    }

    public static IEnumerable<IEnvelopeRule> RulesForMessageType(Type type)
    {
        if (_rulesByMessageType.TryFind(type, out var rules))
        {
            return rules;
        }

        rules = type.GetAllAttributes<ModifyEnvelopeAttribute>().OfType<IEnvelopeRule>().ToList();
        _rulesByMessageType = _rulesByMessageType.AddOrUpdate(type, rules);

        return rules;
    }

    public override string ToString()
    {
        return $"Send to {Sender.Destination}";
    }
}

/// <summary>
/// GH-2966: <see cref="MessageRoute"/> for transports that carry the reply in the same request/response
/// exchange (e.g. HTTP). <c>InvokeAsync&lt;T&gt;</c> reads the reply straight off the protocol response —
/// no <c>ReplyListener&lt;T&gt;</c>, no listener loop on the sender, no cross-loop
/// <c>TaskCompletionSource</c>. Selected at routing-compile time by <see cref="MessageRoute.For"/> when
/// the endpoint implements <see cref="IInlineRequestReplyEndpoint"/>, so there is no per-message branching.
/// </summary>
internal class InlineReplyMessageRoute : MessageRoute
{
    private static readonly string _failureAckTypeName = typeof(FailureAcknowledgement).ToMessageTypeName();
    private readonly IInlineRequestReplyEndpoint _inline;

    public InlineReplyMessageRoute(Type messageType, Endpoint endpoint, IWolverineRuntime runtime)
        : base(messageType, endpoint, runtime)
    {
        _inline = (IInlineRequestReplyEndpoint)endpoint;
    }

    protected override async Task<T> sendAndAwaitReplyAsync<T>(Envelope envelope, MessageBus bus,
        CancellationToken cancellation, TimeSpan timeout)
    {
        envelope.Serializer ??= Serializer;
        var reply = await _inline.InvokeRemoteAsync(envelope, bus.Runtime, cancellation).ConfigureAwait(false);
        return completeReply<T>(reply, bus.Runtime);
    }

    // Translate the reply envelope read straight off the transport response into the caller's T,
    // preserving the brokered request/reply semantics — a receiver handler failure comes back as a
    // FailureAcknowledgement and is rethrown as WolverineRequestReplyException.
    private T completeReply<T>(Envelope reply, IWolverineRuntime runtime)
    {
        if (reply.Message is FailureAcknowledgement directAck)
        {
            throw new WolverineRequestReplyException(directAck.Message);
        }

        if (reply.MessageType == _failureAckTypeName)
        {
            var ack = (FailureAcknowledgement)IntrinsicSerializer.Instance.ReadFromData(typeof(FailureAcknowledgement), reply);
            throw new WolverineRequestReplyException(ack.Message);
        }

        if (typeof(T) == typeof(Acknowledgement))
        {
            return (T)(object)new Acknowledgement { RequestId = reply.ConversationId };
        }

        if (reply.Message is T alreadyTyped)
        {
            return alreadyTyped;
        }

        var serializer = reply.ContentType.IsNotEmpty()
            ? runtime.Options.FindSerializer(reply.ContentType)
            : Serializer;

        var message = serializer.ReadFromData(typeof(T), reply);
        if (message is T typed)
        {
            return typed;
        }

        throw new WolverineRequestReplyException(
            $"Inline request/reply to {Uri} returned a '{reply.MessageType}' reply that could not be read as {typeof(T).FullNameInCode()}");
    }
}
