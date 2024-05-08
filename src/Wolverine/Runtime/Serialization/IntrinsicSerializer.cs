using System.Reflection.Emit;
using JasperFx.Core;
using JasperFx.Core.Reflection;
using Wolverine.Util;

namespace Wolverine.Runtime.Serialization;

public class IntrinsicSerializer : IMessageSerializer
{
    public const string MimeType = "binary/wolverine";

    private ImHashMap<Type, IMessageSerializer> _inner = ImHashMap<Type, IMessageSerializer>.Empty;

    public string ContentType => MimeType;
    public byte[] Write(Envelope envelope)
    {
        var messageType = envelope.Message.GetType();
        if (_inner.TryFind(messageType, out var serializer))
        {
            return serializer.Write(envelope);
        }

        serializer = typeof(IntrinsicSerializer<>).CloseAndBuildAs<IMessageSerializer>(messageType);
        return serializer.Write(envelope);
    }

    public object ReadFromData(Type messageType, Envelope envelope)
    {
        if (_inner.TryFind(messageType, out var serializer))
        {
            return serializer.ReadFromData(envelope.Data);
        }

        serializer = typeof(IntrinsicSerializer<>).CloseAndBuildAs<IMessageSerializer>(messageType);
        _inner = _inner.AddOrUpdate(messageType, serializer);
        return serializer.ReadFromData(envelope.Data);
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
        return WriteMessage(envelope.Message);
    }

    public object ReadFromData(Type messageType, Envelope envelope)
    {
        return T.Read(envelope.Data);
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