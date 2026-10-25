using Microsoft.Extensions.Logging;
using Wolverine.Persistence.Durability.DeadLetterManagement;
using Wolverine.Runtime;

namespace Wolverine.Persistence.Durability;

public class DeadLetterEnvelope
{
    public DeadLetterEnvelope(
        Guid id,
        DateTimeOffset? executionTime,
        Envelope envelope,
        string messageType,
        string receivedAt,
        string source,
        string exceptionType,
        string exceptionMessage,
        DateTimeOffset sentAt,
        bool replayable
    )
    {
        Id = id;
        ExecutionTime = executionTime;
        Envelope = envelope;
        MessageType = messageType;
        ReceivedAt = receivedAt;
        Source = source;
        ExceptionType = exceptionType;
        ExceptionMessage = exceptionMessage;
        SentAt = sentAt;
        Replayable = replayable;
    }

    public Guid Id { get; }
    public DateTimeOffset? ExecutionTime { get; }
    public Envelope Envelope { get; }
    public string MessageType { get; }
    public string ReceivedAt { get; }
    
    /// <summary>
    /// Service name of the original sending application
    /// </summary>
    public string Source { get; }
    public string ExceptionType { get; }
    public string ExceptionMessage { get; }
    public DateTimeOffset SentAt { get; }
    public bool Replayable { get; }

    /// <summary>
    /// The failure group this dead letter belongs to. See <see cref="IDeadLetterFingerprinter"/>. Null for a
    /// dead letter recorded before failure fingerprinting, or by a store that does not fingerprint
    /// </summary>
    public string? FailureGroup { get; set; }

    /// <summary>
    /// The human readable title of <see cref="FailureGroup"/>
    /// </summary>
    public string? FailureTitle { get; set; }

    /// <summary>
    /// When the message was moved to dead letter storage this time
    /// </summary>
    public DateTimeOffset? FailedAt { get; set; }

    /// <summary>
    /// When the message was first moved to dead letter storage, counting failures before a replay
    /// </summary>
    public DateTimeOffset? FirstFailedAt { get; set; }

    /// <summary>
    /// How many times this message has been replayed out of dead letter storage before failing again
    /// </summary>
    public int ReplayCount { get; set; }

    /// <summary>
    /// When this message was last replayed out of dead letter storage, if it ever was
    /// </summary>
    public DateTimeOffset? LastReplayedAt { get; set; }

    /// <summary>
    /// When this dead letter was archived. Null if it is not archived
    /// </summary>
    public DateTimeOffset? ArchivedAt { get; set; }

    /// <summary>
    ///     The actual message body
    /// </summary>
    public object? Message { get; set; }

    internal void TryReadData(IWolverineRuntime runtime)
    {
        // A row whose stored body could not be deserialized comes back as a PlaceHolder with no
        // Destination (see DatabasePersistence.ReadDeadLetterBodyAsync). There is no data to read on
        // such a row, and EndpointFor would be handed a null Uri.
        if (Envelope.Destination is null)
        {
            return;
        }

        if (runtime.Options.HandlerGraph.TryFindMessageType(MessageType, out var messageType))
        {
            var endpoint = runtime.Endpoints.EndpointFor(Envelope.Destination!);
            var serializer = endpoint?.TryFindSerializer(Envelope.ContentType) ??
                             runtime.Options.TryFindSerializer(Envelope.ContentType);

            if (serializer != null)
            {
                try
                {
                    Message = serializer.ReadFromData(messageType, Envelope);
                }
                catch (Exception e)
                {
                    runtime.Logger.LogError(e,
                        "Error trying to deserialize the data for an envelope {Id} being fetched from the dead letter queue storage", Id);
                }
            }
        }
    }
}