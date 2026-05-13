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
    
    private IntrinsicSerializer(){}

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