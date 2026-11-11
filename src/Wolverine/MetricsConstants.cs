namespace Wolverine;

/// <summary>
///     Instrument names, units and tag keys of Wolverine's OpenTelemetry metrics. Every instrument is published on
///     the single <see cref="MeterName" /> meter and named in the dotted <c>wolverine.*</c> form (7.0, GH-5128):
///     dots between namespace segments, underscores inside a multi-word segment.
/// </summary>
public class MetricsConstants
{
    /// <summary>
    ///     The name of the <see cref="System.Diagnostics.Metrics.Meter" /> every Wolverine runtime publishes its
    ///     metrics on, so <c>AddMeter("Wolverine")</c> collects them. The service is identified by the
    ///     OpenTelemetry resource and by the <see cref="SourceKey" /> tag on every measurement, not by the meter
    ///     name (before 7.0 the meter was named <c>Wolverine:{ServiceName}</c>).
    /// </summary>
    public const string MeterName = "Wolverine";

    public const string MessagesSent = "wolverine.messages.sent";
    public const string ExecutionTime = "wolverine.execution.time";
    public const string MessagesSucceeded = "wolverine.messages.succeeded";
    public const string DeadLetterQueue = "wolverine.dead_letter_queue";
    public const string FaultPublishFailures = "wolverine.fault_publish.failures";
    public const string EffectiveMessageTime = "wolverine.effective.time";

    public const string Milliseconds = "Milliseconds";
    public const string Messages = "Messages";
    public const string Connections = "Connections";

    public const string InboxCount = "wolverine.inbox.count";
    public const string OutboxCount = "wolverine.outbox.count";
    public const string ScheduledCount = "wolverine.scheduled.count";

    // Connection budget, tagged by ServerKey. Server-scoped rather than database-scoped: the
    // connections are a resource of the server, shared by every database on it. See #3397.
    public const string DatabaseConnectionCount = "wolverine.database.connection.count";
    public const string DatabaseConnectionBudget = "wolverine.database.connection.budget";

    // GH-4048. Broker lease renewal for envelopes queued but not yet settled under EndpointMode.NativeAck.
    // "lost" is split by LeaseLossStageKey because the two halves mean different things: "not-started" is
    // duplication that was PREVENTED (the queued copy is dropped without being completed or deferred),
    // "executing" is duplication that was REALIZED and can only be reported. A rising "executing" count is
    // the operator's signal that lane depth has outgrown the configured lease.
    public const string LeasesRenewed = "wolverine.leases.renewed";
    public const string LeasesLost = "wolverine.leases.lost";
    public const string LeaseCeilingReached = "wolverine.lease.ceiling_reached";
    public const string LeaseLossStageKey = "lease.loss.stage";

    // GH-4199. Opt-in in-memory duplicate suppression (GH-3710) dropped duplicates with nothing but a
    // LogDebug, so neither question an operator actually asks was answerable: "is my redelivery rate normal"
    // and "is the guard's window big enough". EarlyRotation is the second one -- a rotation forced by the
    // MaxTracked ceiling rather than by the clock means the effective window is SHORTER than the configured
    // one, which is otherwise indistinguishable from a healthy deployment.
    public const string DuplicatesSuppressed = "wolverine.duplicates.suppressed";
    public const string IdempotencyEarlyRotation = "wolverine.idempotency.early_rotation";

    public const string MessageTypeKey = "message.type";
    public const string MessageDestinationKey = "message.destination";
    public const string TenantIdKey = "tenant.id";
    public const string MessagesReceived = "wolverine.messages.received";
    public const string MessagesFailed = "wolverine.execution.failure";
    public const string ExceptionType = "exception.type";
    public const string SourceKey = "source";

    // GH-4445. The opts.Schedules schedule that published an occurrence, so the success / failure /
    // effective-time counters can be sliced per cron job. Bounded cardinality -- schedule names are
    // operator-defined and finite. The occurrence INSTANT is deliberately not here; it is a trace
    // tag only, because one series per firing is unbounded.
    public const string ScheduleNameKey = "schedule.name";

    // GH-5320. Occurrences of a SkipWhileRunning() recurring schedule that came due while the previous
    // occurrence was still being handled, and so were skipped. Tagged with ScheduleNameKey.
    public const string RecurringOccurrencesSkipped = "wolverine.recurring.occurrences_skipped";
    public const string DatabaseKey = "database";
    public const string ServerKey = "server";
}