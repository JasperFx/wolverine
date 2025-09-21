using Microsoft.Extensions.Logging;
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
    public string Source { get; }
    public string ExceptionType { get; }
    public string ExceptionMessage { get; }
    public DateTimeOffset SentAt { get; }
    public bool Replayable { get; }

    /// <summary>
    ///     The actual message body
    /// </summary>
    public object? Message { get; set; }

    internal void TryReadData(IWolverineRuntime runtime)
    {
        if (runtime.Options.HandlerGraph.TryFindMessageType(MessageType, out var messageType))
        {
            var endpoint = runtime.Endpoints.EndpointFor(Envelope.Destination);
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