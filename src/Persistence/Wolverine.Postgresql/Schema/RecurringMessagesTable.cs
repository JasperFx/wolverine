using Weasel.Core;
using Weasel.Postgresql.Tables;
using Wolverine.RDBMS;

namespace Wolverine.Postgresql.Schema;

/// <summary>
/// Tracking table for recurring (cron) message schedules — one row per registered schedule,
/// mapping the schedule name to the envelope id(s) of its pre-scheduled next occurrence plus its
/// pause state. Bookkeeping beside the inbox, never a delivery path: the scheduled inbox row IS
/// the materialized next occurrence. Provisioned only on the Main store and only when
/// <see cref="DurabilitySettings.EnableRecurringMessages" /> is set (i.e. at least one schedule
/// is registered through <c>opts.Schedules</c>).
/// </summary>
internal class RecurringMessagesTable : Table
{
    public RecurringMessagesTable(string schemaName) : base(
        new DbObjectName(schemaName, DatabaseConstants.RecurringMessagesTableName))
    {
        // 250 matches the deduplication-id convention; a schedule name is an identity meant to be
        // legible in the database, not a payload.
        AddColumn(DatabaseConstants.ScheduleName, "varchar(250)").NotNull().AsPrimaryKey();
        AddColumn(DatabaseConstants.CronExpression, "varchar(250)").NotNull();

        // Comma-joined envelope ids ("N" format) — usually one, more when the message type routes
        // to multiple durable subscribers; empty while paused or before the first publish. 2000
        // characters holds ~60 ids, far past any plausible subscriber count for one message type.
        AddColumn(DatabaseConstants.EnvelopeIds, "varchar(2000)").NotNull();
        AddColumn(DatabaseConstants.DeduplicationId, "varchar(250)").AllowNulls();
        AddColumn<DateTimeOffset>(DatabaseConstants.NextOccurrence).AllowNulls();
        AddColumn<bool>(DatabaseConstants.Paused).NotNull();
        AddColumn<DateTimeOffset>(DatabaseConstants.PausedAt).AllowNulls();
        AddColumn<DateTimeOffset>(DatabaseConstants.LastUpdated).NotNull();
    }
}
