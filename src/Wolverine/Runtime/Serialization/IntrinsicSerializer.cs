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

    // IntrinsicSerializer<T> closes over the message type via reflection so
    // user message types that implement ISerializable can be serialized without
    // an additional registration. The CloseAndBuildAs<T> escape valve is
    // dynamic-code-by-design; suppressing at the leaf rather than annotating
    // IMessageSerializer keeps the cascade contained. AOT-clean apps avoid
    // this path: their message types implement ISerializable AND get
    // pre-discovered into _inner before any message is processed (the AOT
    // pillar's source-generated registration story will land that
    // pre-discovery — see #2746 / the AOT publishing guide).
    [UnconditionalSuppressMessage("Trimming", "IL2026",
        Justification = "Closed generic resolved from runtime message type; AOT consumers pre-discover into _inner. See AOT guide.")]
    [UnconditionalSuppressMessage("AOT", "IL3050",
        Justification = "Closed generic resolved from runtime message type; AOT consumers pre-discover into _inner. See AOT guide.")]
    public byte[] Write(Envelope envelope)
    {
        var messageType = envelope.Message!.GetType();
        if (_inner.TryFind(messageType, out var serializer))
        {
            return serializer.Write(envelope);
        }

        serializer = typeof(IntrinsicSerializer<>).CloseAndBuildAs<IMessageSerializer>(messageType);
        _inner = _inner.AddOrUpdate(messageType, serializer);
        return serializer.Write(envelope);
    }

    [UnconditionalSuppressMessage("Trimming", "IL2026",
        Justification = "Closed generic resolved from messageType; AOT consumers pre-discover into _inner. See AOT guide.")]
    [UnconditionalSuppressMessage("AOT", "IL3050",
        Justification = "Closed generic resolved from messageType; AOT consumers pre-discover into _inner. See AOT guide.")]
    public object ReadFromData(Type messageType, Envelope envelope)
    {
        if (_inner.TryFind(messageType, out var serializer))
        {
            return serializer.ReadFromData(envelope.Data!);
        }

        serializer = typeof(IntrinsicSerializer<>).CloseAndBuildAs<IMessageSerializer>(messageType);
        _inner = _inner.AddOrUpdate(messageType, serializer);
        return serializer.ReadFromData(envelope.Data!);
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
    [UnconditionalSuppressMessage("Trimming", "IL2026",
        Justification = "Pre-populating the IntrinsicSerializer<T> cache at handler-graph compile time; same suppression as Write/ReadFromData. See AOT guide / #2769.")]
    [UnconditionalSuppressMessage("AOT", "IL3050",
        Justification = "Pre-populating the IntrinsicSerializer<T> cache at handler-graph compile time; same suppression as Write/ReadFromData. See AOT guide / #2769.")]
    internal void Prepopulate(IEnumerable<Type>? messageTypes)
    {
        if (messageTypes == null) return;

        foreach (var messageType in messageTypes)
        {
            if (messageType == null) continue;
            if (!messageType.CanBeCastTo(typeof(ISerializable))) continue;
            if (_inner.TryFind(messageType, out _)) continue;

            var serializer = typeof(IntrinsicSerializer<>).CloseAndBuildAs<IMessageSerializer>(messageType);
            _inner = _inner.AddOrUpdate(messageType, serializer);
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