using System.Diagnostics;
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
    private readonly Endpoint _endpoint;

    public MessageRoute(Type messageType, Endpoint endpoint, IWolverineRuntime runtime)
    {
        IsLocal = endpoint is LocalQueue;
        _replyTracker = runtime.Replies;
        _partitioning = runtime.Options.MessagePartitioning;

        if (WolverineSystemPart.WithinDescription)
        {
            Sender = endpoint.Agent;
        }
        else
        {
            Sender = endpoint.Agent ?? throw new ArgumentOutOfRangeException(nameof(endpoint), $"Endpoint {endpoint.Uri} does not have an active sending agent. Message type: {messageType.FullNameInCode()}");
        }

        IsLocal = endpoint is LocalQueue;

        if (messageType.CanBeCastTo(typeof(ISerializable)))
        {
            Serializer = typeof(IntrinsicSerializer<>).CloseAndBuildAs<IMessageSerializer>(messageType);
        }
        else if (WolverineSystemPart.WithinDescription)
        {
            Serializer = endpoint.DefaultSerializer;
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

    public IMessageSerializer Serializer { get; }
    public ISendingAgent Sender { get; }

    public IList<IEnvelopeRule> Rules { get; } = new List<IEnvelopeRule>();

    public Task InvokeAsync(object message, MessageBus bus, CancellationToken cancellation = default,
        TimeSpan? timeout = null, DeliveryOptions? options = null)
    {
        return InvokeAsync<Acknowledgement>(message, bus, cancellation, timeout, options);
    }

    public Envelope CreateForSending(object message, DeliveryOptions? options, ISendingAgent localDurableQueue,
        WolverineRuntime runtime, string? topicName)
    {
        var envelope = new Envelope(message, Sender)
        {
            Serializer = Serializer,
            ContentType = Serializer?.ContentType,
            TopicName = topicName
        };

        if (Sender.Endpoint is LocalQueue)
        {
            envelope.Status = EnvelopeStatus.Incoming;
        }

        if (options != null && options.ContentType.IsNotEmpty() && options.ContentType != Serializer.ContentType)
        {
            envelope.Serializer = runtime.Options.FindSerializer(options.ContentType);
            envelope.ContentType = envelope.Serializer.ContentType;
        }

        // Apply application wide message grouping policies
        envelope.GroupId = _partitioning.DetermineGroupId(envelope);

        foreach (var rule in Rules) rule.Modify(envelope);

        // Delivery options win
        options?.Override(envelope);

        // Will need the topic persisted, see https://github.com/JasperFx/wolverine/issues/1100
        if (Sender.Endpoint.RoutingType == RoutingMode.ByTopic && envelope.TopicName.IsEmpty())
        {
            envelope.TopicName = TopicRouting.DetermineTopicName(envelope);
        }

        if (envelope.IsScheduledForLater(DateTimeOffset.UtcNow))
        {
            if (IsLocal)
            {
                envelope.Status = EnvelopeStatus.Scheduled;
                envelope.OwnerId = TransportConstants.AnyNode;
                runtime.Logger.LogDebug("Envelope {EnvelopeId} ({MessageType}) marked as Scheduled for local execution at {Destination}", envelope.Id, envelope.MessageType, envelope.Destination);
            }
            else if (!Sender.SupportsNativeScheduledSend)
            {
                runtime.Logger.LogDebug("Envelope {EnvelopeId} ({MessageType}) wrapped for durable scheduled send to {Destination} (transport does not support native scheduling)", envelope.Id, envelope.MessageType, envelope.Destination);
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
            TopicName = topicName
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

        var waiter = _replyTracker.RegisterListener<T>(envelope, cancellation, timeout.Value);

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