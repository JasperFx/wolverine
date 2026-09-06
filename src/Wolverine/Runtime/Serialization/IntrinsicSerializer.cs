using System.Diagnostics.CodeAnalysis;
using ImTools;
using JasperFx.Core;
using JasperFx.Core.Reflection;

namespace Wolverine.Runtime.Serialization;

public class IntrinsicSerializer : IMessageSerializer
{
    public const string MimeType = "binary/wolverine";

    private ImHashMap<Type, IMessageSerializer> _inner = ImHashMap<Type, IMessageSerializer>.Empty;

    public static readonly IntrinsicSerializer Instance = new();

    private IntrinsicSerializer()
    {
        // GH-4287. Seed the framework's own ISerializable message types by DIRECT construction.
        // Under Native AOT the reflective CloseAndBuildAs below throws MissingMethodException --
        // the closed generic's constructor metadata is trimmed -- and HandlerGraph.Compile's
        // Prepopulate call hit exactly that for Acknowledgement/FailureAcknowledgement, killing
        // startup for every AOT-published app. Direct construction both roots the closed generics
        // for the AOT compiler and makes the seeding free at JIT time. User-defined ISerializable
        // message types under AOT remain the source-generated pre-discovery story (#2769).
        _inner = _inner
            .AddOrUpdate(typeof(RemoteInvocation.Acknowledgement), new IntrinsicSerializer<RemoteInvocation.Acknowledgement>())
            .AddOrUpdate(typeof(RemoteInvocation.FailureAcknowledgement), new IntrinsicSerializer<RemoteInvocation.FailureAcknowledgement>())
            .AddOrUpdate(typeof(PlaceHolder), new IntrinsicSerializer<PlaceHolder>())
            .AddOrUpdate(typeof(Agents.NodeRecord), new IntrinsicSerializer<Agents.NodeRecord>())
            .AddOrUpdate(typeof(Agents.AgentsStarted), new IntrinsicSerializer<Agents.AgentsStarted>())
            .AddOrUpdate(typeof(Agents.AgentsStopped), new IntrinsicSerializer<Agents.AgentsStopped>())
            .AddOrUpdate(typeof(Agents.StartAgents), new IntrinsicSerializer<Agents.StartAgents>())
            .AddOrUpdate(typeof(Agents.StopAgents), new IntrinsicSerializer<Agents.StopAgents>())
            .AddOrUpdate(typeof(Agents.StartAgent), new IntrinsicSerializer<Agents.StartAgent>())
            .AddOrUpdate(typeof(Agents.StopAgent), new IntrinsicSerializer<Agents.StopAgent>())
            .AddOrUpdate(typeof(Agents.QueryAgentPresence), new IntrinsicSerializer<Agents.QueryAgentPresence>())
            .AddOrUpdate(typeof(Agents.AgentPresenceReport), new IntrinsicSerializer<Agents.AgentPresenceReport>())
            .AddOrUpdate(typeof(Agents.CheckAgentHealth), new IntrinsicSerializer<Agents.CheckAgentHealth>());
    }

    public string ContentType => MimeType;

    // Serialization goes through SerializerFor, which is the single place that may close
    // IntrinsicSerializer<T> over a runtime message type and the only place carrying the
    // dynamic-code suppression for it.
    public byte[] Write(Envelope envelope)
    {
        return SerializerFor(envelope.Message!.GetType()).Write(envelope);
    }

    public object ReadFromData(Type messageType, Envelope envelope)
    {
        return SerializerFor(messageType).ReadFromData(envelope.Data!);
    }

    /// <summary>
    ///     The <see cref="IntrinsicSerializer{T}" /> for this message type, from the cache when it is already
    ///     known and by closing the open generic when it is not.
    ///
    ///     GH-4232: every caller has to come through here rather than closing the generic itself. The
    ///     framework's own ISerializable types are seeded into the cache by DIRECT construction in the
    ///     constructor above precisely because the reflective close throws MissingMethodException under
    ///     Native AOT, and a caller that skips the cache throws for those types even though a perfectly
    ///     good instance is sitting in it. <see cref="Wolverine.Runtime.Routing.MessageRoute" /> did exactly
    ///     that, so every AOT-published app with any external endpoint died building the route for
    ///     FailureAcknowledgement -- past the GH-4287 fixes, and past the boot the AOT publish smoke asserts,
    ///     because that smoke only dispatches locally and never builds an external route.
    /// </summary>
    [UnconditionalSuppressMessage("Trimming", "IL2026",
        Justification = "Closed generic resolved from a runtime message type; the framework's own types are pre-seeded and AOT consumers pre-discover the rest into _inner. See AOT guide.")]
    [UnconditionalSuppressMessage("AOT", "IL3050",
        Justification = "Closed generic resolved from a runtime message type; the framework's own types are pre-seeded and AOT consumers pre-discover the rest into _inner. See AOT guide.")]
    internal IMessageSerializer SerializerFor(Type messageType)
    {
        if (_inner.TryFind(messageType, out var serializer))
        {
            return serializer;
        }

        serializer = typeof(IntrinsicSerializer<>).CloseAndBuildAs<IMessageSerializer>(messageType);
        _inner = _inner.AddOrUpdate(messageType, serializer);
        return serializer;
    }

    public object ReadFromData(byte[] data)
    {
        throw new NotSupportedException();
    }

    public byte[] WriteMessage(object message)
    {
        throw new NotSupportedException();
    }

    /// <summary>
    /// Pre-populate the per-message-type IntrinsicSerializer&lt;T&gt; cache with the
    /// supplied message types that implement <see cref="ISerializable"/>. Called
    /// from <see cref="Wolverine.Runtime.Handlers.HandlerGraph.Compile"/> after
    /// handler-graph compilation so the per-message Write/ReadFromData hot path
    /// never pays the first-occurrence CloseAndBuildAs over
    /// IntrinsicSerializer&lt;T&gt;. Closes the AOT story for ISerializable
    /// dispatch from AOT pillar issue #2769.
    /// </summary>
    /// <remarks>
    /// Skips message types that don't implement ISerializable — those are served
    /// by the default JSON serializer (or whatever the endpoint configures).
    /// Tolerates duplicates and a null source.
    /// </remarks>
    /// <param name="messageTypes">Message types to resolve and cache.</param>
    internal void Prepopulate(IEnumerable<Type>? messageTypes)
    {
        if (messageTypes == null) return;

        foreach (var messageType in messageTypes)
        {
            if (messageType == null) continue;
            if (!messageType.CanBeCastTo(typeof(ISerializable))) continue;
            SerializerFor(messageType);
        }
    }
}

internal class IntrinsicSerializer<T> : IMessageSerializer where T : ISerializable
{
    public string ContentType => IntrinsicSerializer.MimeType;
    public byte[] Write(Envelope envelope)
    {
        return WriteMessage(envelope.Message!);
    }

    public object ReadFromData(Type messageType, Envelope envelope)
    {
        return T.Read(envelope.Data!);
    }

    public object ReadFromData(byte[] data)
    {
        return T.Read(data);
    }

    public byte[] WriteMessage(object message)
    {
        if (message is ISerializable s)
        {
            return s.Write();
        }

        throw new ArgumentOutOfRangeException(nameof(message),
            $"The message type {message.GetType().FullNameInCode()} does not implement {nameof(ISerializable)}");

    }
}