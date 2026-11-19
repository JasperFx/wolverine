using System.Buffers;
using System.Text;
using System.Text.Json;

namespace Wolverine.Persistence.Sagas;

/// <summary>
///     GH-5665. What a message did to a saga that records its history (<see cref="RecordSagaHistoryAttribute" />)
/// </summary>
public enum SagaHistoryOutcome
{
    /// <summary>The message started the saga</summary>
    Started,

    /// <summary>The message was handled, and the saga is still running</summary>
    Continued,

    /// <summary>The message completed the saga</summary>
    Completed,

    /// <summary>The message was a <c>Fault&lt;T&gt;</c> the saga compensated</summary>
    Compensated,

    /// <summary>
    ///     The message reached a saga that had already completed, and went to the saga's <c>NotFound</c> method. Only a
    ///     retained saga (<see cref="RetainWhenCompletedAttribute" />) is still there to record it
    /// </summary>
    NotFound
}

/// <summary>
///     GH-5665. Which part of a saga's generated handler is recording a history row. Used by generated code.
/// </summary>
public enum SagaHistoryStep
{
    /// <summary>A starting message</summary>
    Start,

    /// <summary>A message for a running saga</summary>
    Continue,

    /// <summary>A <c>Fault&lt;T&gt;</c> handled by the saga's <c>Compensate</c> methods</summary>
    Compensate,

    /// <summary>A message for a completed, retained saga, handled by the saga's <c>NotFound</c> method</summary>
    NotFound
}

/// <summary>GH-5665. One message a saga sent while it handled a message.</summary>
/// <param name="MessageType">Wolverine's message type name of the sent message</param>
/// <param name="Id">The sent envelope's id</param>
/// <param name="Destination">Where it was sent, when that was known</param>
/// <param name="ScheduledTime">When it is scheduled to be delivered, for a scheduled message or a saga timeout</param>
public sealed record SagaHistorySentMessage(string MessageType, Guid Id, string? Destination, DateTimeOffset? ScheduledTime);

/// <summary>
///     GH-5665. One row of a document saga's message history: the handled message, when, by which handler, with what
///     outcome, and what the saga sent. Never the saga's state.
/// </summary>
public sealed record SagaHistoryRecord
{
    /// <summary>The row's own id, time ordered</summary>
    public Guid Id { get; init; } = Guid.CreateVersion7();

    /// <summary>The saga type's full name</summary>
    public required string SagaType { get; init; }

    /// <summary>The saga's identity, as text, the same way the saga id header and the dead letters carry it</summary>
    public required string SagaId { get; init; }

    /// <summary>The tenant the message was handled for, or null for the default tenant</summary>
    public string? TenantId { get; init; }

    /// <summary>The handled envelope's id</summary>
    public Guid? MessageId { get; init; }

    /// <summary>Wolverine's message type name of the handled message</summary>
    public required string MessageType { get; init; }

    /// <summary>The saga method or methods that handled it, e.g. <c>OrderSaga.Handle</c></summary>
    public required string Handler { get; init; }

    /// <summary>What the message did to the saga</summary>
    public SagaHistoryOutcome Outcome { get; init; }

    /// <summary>Whether this message completed the saga. Also true for a compensation that completed it</summary>
    public bool CompletesSaga { get; init; }

    /// <summary>When the message was handled</summary>
    public DateTimeOffset RecordedAt { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>The messages the saga sent while it handled this one, scheduled timeouts included</summary>
    public IReadOnlyList<SagaHistorySentMessage> SentMessages { get; init; } = [];

    /// <summary>
    ///     The sent messages as the JSON array kept in the <c>sent_messages</c> column. Written by hand rather than
    ///     through a serializer so that it works in a Native AOT application.
    /// </summary>
    public string SentMessagesJson()
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writeSentMessages(writer, SentMessages);
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    /// <summary>Reads the <c>sent_messages</c> column back</summary>
    public static IReadOnlyList<SagaHistorySentMessage> ParseSentMessages(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];

        using var document = JsonDocument.Parse(json);
        var list = new List<SagaHistorySentMessage>();
        foreach (var element in document.RootElement.EnumerateArray())
        {
            list.Add(new SagaHistorySentMessage(
                element.GetProperty(nameof(SagaHistorySentMessage.MessageType)).GetString()!,
                element.GetProperty(nameof(SagaHistorySentMessage.Id)).GetGuid(),
                element.TryGetProperty(nameof(SagaHistorySentMessage.Destination), out var destination) &&
                destination.ValueKind == JsonValueKind.String
                    ? destination.GetString()
                    : null,
                element.TryGetProperty(nameof(SagaHistorySentMessage.ScheduledTime), out var scheduled) &&
                scheduled.ValueKind == JsonValueKind.String
                    ? scheduled.GetDateTimeOffset()
                    : null));
        }

        return list;
    }

    /// <summary>
    ///     The row as the <see cref="SagaHistoryEvent.Data" /> of a history page: an object with every member of this
    ///     record, in the same Pascal case member names
    /// </summary>
    public JsonElement ToJson()
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString(nameof(Id), Id);
            writer.WriteString(nameof(SagaType), SagaType);
            writer.WriteString(nameof(SagaId), SagaId);
            if (TenantId == null) writer.WriteNull(nameof(TenantId));
            else writer.WriteString(nameof(TenantId), TenantId);
            if (MessageId.HasValue) writer.WriteString(nameof(MessageId), MessageId.Value);
            else writer.WriteNull(nameof(MessageId));
            writer.WriteString(nameof(MessageType), MessageType);
            writer.WriteString(nameof(Handler), Handler);
            writer.WriteString(nameof(Outcome), Outcome.ToString());
            writer.WriteBoolean(nameof(CompletesSaga), CompletesSaga);
            writer.WriteString(nameof(RecordedAt), RecordedAt);
            writer.WritePropertyName(nameof(SentMessages));
            writeSentMessages(writer, SentMessages);
            writer.WriteEndObject();
        }

        using var document = JsonDocument.Parse(buffer.WrittenMemory);
        return document.RootElement.Clone();
    }

    private static void writeSentMessages(Utf8JsonWriter writer, IReadOnlyList<SagaHistorySentMessage> messages)
    {
        writer.WriteStartArray();
        foreach (var message in messages)
        {
            writer.WriteStartObject();
            writer.WriteString(nameof(SagaHistorySentMessage.MessageType), message.MessageType);
            writer.WriteString(nameof(SagaHistorySentMessage.Id), message.Id);
            if (message.Destination == null) writer.WriteNull(nameof(SagaHistorySentMessage.Destination));
            else writer.WriteString(nameof(SagaHistorySentMessage.Destination), message.Destination);
            if (message.ScheduledTime.HasValue)
                writer.WriteString(nameof(SagaHistorySentMessage.ScheduledTime), message.ScheduledTime.Value);
            else writer.WriteNull(nameof(SagaHistorySentMessage.ScheduledTime));
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
    }
}

/// <summary>
///     GH-5665. An envelope transaction that can write a saga's history row in the same transaction as the saga
///     itself. Implemented by the outbox enlistments of the database message stores and of Marten, Polecat, Fisher and
///     EF Core.
/// </summary>
public interface ISagaHistoryTransaction
{
    /// <summary>
    ///     Write the row in this transaction. False when this transaction cannot take it after all, and the row has to
    ///     be written on its own.
    /// </summary>
    Task<bool> TryPersistSagaHistoryAsync(SagaHistoryRecord record, CancellationToken token);

    /// <summary>
    ///     Delete the saga's history in this transaction, for a saga that is deleted as it completes. False when this
    ///     transaction cannot do it, and the delete has to run on its own.
    /// </summary>
    Task<bool> TryDeleteSagaHistoryAsync(string sagaType, string sagaId, string? tenantId, CancellationToken token);
}

/// <summary>
///     GH-5665. A message store that keeps the <c>wolverine_saga_history</c> table: written on its own where a saga's
///     storage does not enlist in the outbox, read for <see cref="ISagaHistoryDiagnostics" />, and swept with retained
///     sagas.
/// </summary>
public interface ISagaHistoryStore
{
    /// <summary>Write one row, in a transaction of its own</summary>
    Task RecordSagaHistoryAsync(SagaHistoryRecord record, CancellationToken token);

    /// <summary>Delete one saga's history, in a transaction of its own</summary>
    Task DeleteSagaHistoryAsync(string sagaType, string sagaId, string? tenantId, CancellationToken token);

    /// <summary>
    ///     One saga's history, oldest first, skipping <paramref name="skip" /> rows and taking at most
    ///     <paramref name="take" />, with the number of rows in all. A null <paramref name="tenantId" /> reads every
    ///     tenant's rows.
    /// </summary>
    Task<SagaHistoryRows> ReadSagaHistoryAsync(string sagaType, string sagaId, string? tenantId, int skip, int take,
        CancellationToken token);

    /// <summary>
    ///     Delete the history of every saga of <paramref name="sagaType" /> that completed before
    ///     <paramref name="completedBefore" />, as the retention sweep deletes the sagas themselves
    /// </summary>
    /// <returns>The number of rows deleted</returns>
    Task<long> DeleteHistoryOfSagasCompletedBeforeAsync(string sagaType, DateTimeOffset completedBefore,
        CancellationToken token);
}

/// <summary>GH-5665. A page of one saga's history rows, and how many there are in all</summary>
public sealed record SagaHistoryRows(IReadOnlyList<SagaHistoryRecord> Records, long TotalCount);
