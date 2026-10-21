using Bobcat;
using Bobcat.Engine;
using JasperFx.Events;
using Wolverine.Tracking;

namespace Wolverine.Bobcat;

/// <summary>
/// The tracked session's account of a scenario: every envelope record, in order (GH-4837).
/// </summary>
/// <remarks>
/// <para>
/// <b>An account, not a claim.</b> <c>ThenMessageSent</c> and friends are the judged steps; this is
/// what diagnoses a red asynchronous-messaging spec — what was sent where, what failed, what was
/// retried and what went to the error queue — so it rides alongside the verdict as a Bobcat scenario
/// report, written out only when the scenario fails (<see cref="ReportVisibility.OnFailure" />). A
/// green suite pays nothing for it.
/// </para>
/// <para>
/// <c>at (ms)</c> is the session's own clock (<see cref="EnvelopeRecord.SessionTime" />). A failed
/// execution or a dead-lettered envelope is a red cell inside the otherwise informational table.
/// </para>
/// <para>
/// GH-5106. The trailing columns are the identifiers the rest of the system's telemetry is keyed on, so a
/// reader (a coding agent above all) can go from a row to the OpenTelemetry trace, the handler's logs and the
/// exception detail: the envelope's <c>message id</c>, its <c>correlation id</c>, the <c>causation id</c> of
/// the message that caused it (blank for a message the act sent itself), the <c>saga id</c> where there is
/// one, and the <c>trace id</c> and <c>activity id</c> of the OpenTelemetry activity the record was made in.
/// </para>
/// </remarks>
public sealed class MessageActivityReport : TableReport
{
    public override string Title => "Message activity";

    // GH-5077: a record of a message scheduled for later says so, whichever event a transport recorded it as
    private static string eventOf(EnvelopeRecord record)
        => record.WasScheduled && record.MessageEventType != MessageEventType.Scheduled
            ? $"{record.MessageEventType} (scheduled)"
            : record.MessageEventType.ToString();

    /// <summary>Add every record of <paramref name="session" />.</summary>
    public void Add(ITrackedSession session) => Add(session, Array.Empty<object>());

    /// <summary>
    /// Add every record of <paramref name="session" />, marking the rows of the act's own messages
    /// (wolverine#5077): what the act dispatched rather than what the handling produced.
    /// </summary>
    public void Add(ITrackedSession session, IReadOnlyList<object> ownMessages)
    {
        foreach (var record in session.AllRecordsInOrder())
        {
            var failed = record.MessageEventType is MessageEventType.MessageFailed or MessageEventType.MovedToErrorQueue
                || record.Exception is not null;

            Row(new List<CellResult>
            {
                new("at (ms)", ResultStatus.ok, record.SessionTime.ToString()),
                failed
                    ? new CellResult("event", ResultStatus.failed)
                    {
                        Expected = "",
                        Actual = eventOf(record),
                        // GH-5077: the exception type too, which is what a troubleshooter looks for first
                        Note = record.Exception is { } exception ? $"{exception.GetType().Name}: {exception.Message}" : null
                    }
                    : new CellResult("event", ResultStatus.ok, eventOf(record)),
                new("message", ResultStatus.ok, (record.Message?.GetType().Name ?? record.Envelope?.MessageType ?? "") +
                                                (record.Message is { } message && ownMessages.Contains(message, ReferenceEqualityComparer.Instance)
                                                    ? " (the act's own)"
                                                    : "")),
                new("destination", ResultStatus.ok, record.Envelope?.Destination?.ToString() ?? ""),
                new("attempt", ResultStatus.ok, record.AttemptNumber.ToString()),
                new("service", ResultStatus.ok, record.ServiceName ?? ""),
                new("message id", ResultStatus.ok, record.Envelope is { } envelope ? envelope.Id.ToString() : ""),
                new("correlation id", ResultStatus.ok, record.Envelope?.CorrelationId ?? ""),
                new("causation id", ResultStatus.ok, causationOf(record.Envelope)),
                new("saga id", ResultStatus.ok, record.Envelope?.SagaId ?? ""),
                new("trace id", ResultStatus.ok, record.RootId ?? ""),
                new("activity id", ResultStatus.ok, record.ActivityId ?? "")
            });
        }
    }

    // A message caused by another carries that message's id as its conversation id; one that starts a chain
    // carries its own, or none at all
    private static string causationOf(Envelope? envelope)
        => envelope is null || envelope.ConversationId == Guid.Empty || envelope.ConversationId == envelope.Id
            ? ""
            : envelope.ConversationId.ToString();
}

/// <summary>
/// Every event an act appended, in order — the whole of what the stream gained, beside a
/// <c>ThenEvents</c> that may only have asked about some of it (GH-4835). Written out on failure.
/// </summary>
public sealed class AppendedEventsReport : TableReport
{
    public override string Title => "Events appended";

    /// <summary>Add the events one act appended.</summary>
    public void Add(IReadOnlyList<IEvent> events)
    {
        foreach (var e in events)
        {
            // In the scenario's own vocabulary: the stream and the values read by the names the steps
            // above used, and "Named values" beside this report says what each name stands for
            Row(("stream", (object?)(e.StreamKey ?? (e.StreamId == Guid.Empty ? "" : ScenarioValues.Format(e.StreamId)))),
                ("version", e.Version),
                ("event", e.Data.GetType().Name),
                ("data", ScenarioValues.DescribeProperties(e.Data)));
        }
    }
}
