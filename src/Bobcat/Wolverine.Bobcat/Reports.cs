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
/// </remarks>
public sealed class MessageActivityReport : TableReport
{
    public override string Title => "Message activity";

    /// <summary>Add every record of <paramref name="session" />.</summary>
    public void Add(ITrackedSession session)
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
                        Actual = record.MessageEventType.ToString(),
                        Note = record.Exception?.Message
                    }
                    : new CellResult("event", ResultStatus.ok, record.MessageEventType.ToString()),
                new("message", ResultStatus.ok, record.Message?.GetType().Name ?? record.Envelope?.MessageType ?? ""),
                new("destination", ResultStatus.ok, record.Envelope?.Destination?.ToString() ?? ""),
                new("attempt", ResultStatus.ok, record.AttemptNumber.ToString()),
                new("service", ResultStatus.ok, record.ServiceName ?? "")
            });
        }
    }
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
