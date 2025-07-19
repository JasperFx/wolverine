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
///  Async version of <seealso cref="IMessageSerializer"/>
/// </summary>
public interface IAsyncMessageSerializer : IMessageSerializer
{
    ValueTask<byte[]> WriteAsync(Envelope envelope);

    ValueTask<object?> ReadFromDataAsync(Type messageType, Envelope envelope);
}