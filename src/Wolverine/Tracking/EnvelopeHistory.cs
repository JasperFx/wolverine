using Wolverine.Transports;

namespace Wolverine.Tracking;

internal class EnvelopeHistory
{
    private readonly List<EnvelopeRecord> _records = new();

    public EnvelopeHistory(Guid envelopeId)
    {
        EnvelopeId = envelopeId;
    }

    public Guid EnvelopeId { get; }


    public object? Message
    {
        get
        {
            return _records
                .FirstOrDefault(x => x.Envelope.Message != null)?.Envelope.Message;
        }
    }

    public IEnumerable<EnvelopeRecord> Records => _records;

    private EnvelopeRecord? lastOf(MessageEventType eventType)
    {
        return _records.LastOrDefault(x => x.MessageEventType == eventType);
    }

    private void markLastCompleted(MessageEventType eventType)
    {
        var record = lastOf(eventType);
        if (record != null)
        {
            record.IsComplete = true;
        }
    }


    /// <summary>
    ///     Tracks activity for coordinating the testing of a single Wolverine
    ///     application
    /// </summary>
    /// <param name="eventType"></param>
    /// <param name="envelope"></param>
    /// <param name="sessionTime"></param>
    /// <param name="serviceName"></param>
    /// <param name="exception"></param>
    // ReSharper disable once CyclomaticComplexity
    public void RecordLocally(EnvelopeRecord record)
    {
        switch (record.MessageEventType)
        {
            case MessageEventType.Sent:
                // Not tracking anything outgoing
                // when it's testing locally
                if (record.Envelope.Destination?.Scheme != TransportConstants.Local ||
                    record.Envelope.MessageType == TransportConstants.ScheduledEnvelope)
                {
                    record.IsComplete = true;
                }

                if (record.Envelope.Status == EnvelopeStatus.Scheduled)
                {
                    record.IsComplete = true;
                }

                break;

            case MessageEventType.Received:
                if (record.Envelope.Destination?.Scheme == TransportConstants.Local)
                {
                    markLastCompleted(MessageEventType.Sent);
                }

                break;

            case MessageEventType.ExecutionStarted:
                // Nothing special here
                break;


            case MessageEventType.ExecutionFinished:
                markLastCompleted(MessageEventType.ExecutionStarted);
                record.IsComplete = true;
                break;

            case MessageEventType.NoHandlers:
            case MessageEventType.NoRoutes:
            case MessageEventType.MessageFailed:
            case MessageEventType.MessageSucceeded:
            case MessageEventType.MovedToErrorQueue:
                // The message is complete
                foreach (var envelopeRecord in _records) envelopeRecord.IsComplete = true;

                record.IsComplete = true;

                break;
            
            case MessageEventType.Requeued:
                // Do nothing, just informative
                break;

            default:
                throw new ArgumentOutOfRangeException(nameof(record.MessageEventType), record.MessageEventType, null);
        }

        _records.Add(record);
    }

    public void RecordCrossApplication(EnvelopeRecord record)
    {
        switch (record.MessageEventType)
        {
            case MessageEventType.Sent:
                if (record.Envelope.Status == EnvelopeStatus.Scheduled)
                {
                    record.IsComplete = true;
                }

                // This can be out of order with Rabbit MQ *somehow*, so:
                var received = _records.LastOrDefault(x => x.MessageEventType == MessageEventType.Received);
                if (received != null)
                {
                    record.IsComplete = true;
                }

                break;

            case MessageEventType.ExecutionStarted:
                break;

            case MessageEventType.Received:
                markLastCompleted(MessageEventType.Sent);
                break;


            case MessageEventType.ExecutionFinished:
                markLastCompleted(MessageEventType.ExecutionStarted, record.UniqueNodeId);
                record.IsComplete = true;
                break;

            case MessageEventType.MovedToErrorQueue:
            case MessageEventType.MessageFailed:
            case MessageEventType.MessageSucceeded:
                // The message is complete
                foreach (var envelopeRecord in _records.ToArray().Where(x => x.UniqueNodeId == record.UniqueNodeId))
                    envelopeRecord.IsComplete = true;

                record.IsComplete = true;

                break;

            case MessageEventType.NoHandlers:
            case MessageEventType.NoRoutes:
            case MessageEventType.Requeued:

                break;

            default:
                throw new ArgumentOutOfRangeException(nameof(record.MessageEventType), record.MessageEventType, null);
        }

        _records.Add(record);
    }

    private void markLastCompleted(MessageEventType eventType, Guid uniqueNodeId)
    {
        var record = _records.LastOrDefault(x => x.MessageEventType == eventType && x.UniqueNodeId == uniqueNodeId);
        if (record != null)
        {
            record.IsComplete = true;
        }
    }


    public bool IsComplete()
    {
        return _records.ToArray().All(x => x.IsComplete);
    }


    public bool Has(MessageEventType eventType)
    {
        return _records.ToArray().Any(x => x.MessageEventType == eventType);
    }

    public object? MessageFor(MessageEventType eventType)
    {
        return _records.Where(x => x.MessageEventType == eventType)
            .LastOrDefault(x => x.Envelope.Message != null)?.Envelope.Message;
    }

    public override string ToString()
    {
        return $"{nameof(EnvelopeId)}: {EnvelopeId}";
    }
}