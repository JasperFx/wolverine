namespace Wolverine.Runtime.Serialization;

/// <summary>
///     Wolverine's primary interface for message serialization
/// </summary>
public interface IMessageSerializer
{
    string ContentType { get; }

    byte[] Write(Envelope envelope);

    object ReadFromData(Type messageType, Envelope envelope);

    object ReadFromData(byte[] data);

    byte[] WriteMessage(object message);
}

/// <summary>
/// Marker interface that lets Wolverine know that the message serializer
/// may also unwrap metadata on the Envelope. For protocols like CloudEvents
/// that smuggle message metadata through the transport's message body
/// </summary>
public interface IUnwrapsMetadataMessageSerializer : IMessageSerializer
{
    void Unwrap(Envelope envelope);
}

/// <summary>
///  Async version of <seealso cref="IMessageSerializer"/>
/// </summary>
public interface IAsyncMessageSerializer : IMessageSerializer
{
    ValueTask<byte[]> WriteAsync(Envelope envelope);

    ValueTask<object?> ReadFromDataAsync(Type messageType, Envelope envelope);
}