using JasperFx.Core.Reflection;

namespace Wolverine.Tracking;

public class RecordCollection
{
    private readonly MessageEventType _messageEventType;
    private readonly TrackedSession _parent;

    internal RecordCollection(MessageEventType eventType, TrackedSession parent)
    {
        _messageEventType = eventType;
        _parent = parent;
    }
    
    public EnvelopeRecord SingleRecord<T>()
    {
        var records = RecordsInOrder().Where(x => x.Message is T).ToArray();
        switch (records.Length)
        {
            case 0:
                throw new Exception(
                    _parent.BuildActivityMessage($"No messages of type {typeof(T).FullNameInCode()} were received"));

            case 1:
                return records.Single();

            default:
                throw new Exception(_parent.BuildActivityMessage(
                    $"Received {records.Length} messages of type {typeof(T).FullNameInCode()}"));
        }
    }

    public T SingleMessage<T>()
    {
        var records = RecordsInOrder().Where(x => x.Message is T).ToArray();
        switch (records.Length)
        {
            case 0:
                throw new Exception(
                    _parent.BuildActivityMessage($"No messages of type {typeof(T).FullNameInCode()} were received"));

            case 1:
                return (T)records.Single().Message!;

            default:
                throw new Exception(_parent.BuildActivityMessage(
                    $"Received {records.Length} messages of type {typeof(T).FullNameInCode()}"));
        }
    }

    /// <summary>
    ///     Find the expected single envelope where the message type is T
    /// </summary>
    /// <typeparam name="T"></typeparam>
    /// <returns></returns>
    /// <exception cref="Exception"></exception>
    public Envelope SingleEnvelope<T>()
    {
        var records = RecordsInOrder().Where(x => x.Message is T).ToArray();
        switch (records.Length)
        {
            case 0:
                throw new Exception(
                    _parent.BuildActivityMessage($"No messages of type {typeof(T).FullNameInCode()} were received"));

            case 1:
                return records.Single().Envelope;

            default:
                throw new Exception(_parent.BuildActivityMessage(
                    $"Received {records.Length} messages of type {typeof(T).FullNameInCode()}"));
        }
    }

    public IEnumerable<T> MessagesOf<T>()
    {
        return RecordsInOrder().Select(x => x.Message).OfType<T>();
    }

    public IEnumerable<object> AllMessages()
    {
        return RecordsInOrder().Select(x => x.Message).Where(x => x != null)!;
    }

    public IEnumerable<EnvelopeRecord> RecordsInOrder()
    {
        return _parent.AllRecordsInOrder().Where(x => x.MessageEventType == _messageEventType);
    }

    public IEnumerable<Envelope> Envelopes()
    {
        return RecordsInOrder().Select(x => x.Envelope);
    }
}