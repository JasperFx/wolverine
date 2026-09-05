using Weasel.Sqlite;
using Weasel.Sqlite.Tables;
using Wolverine.RDBMS;

namespace Wolverine.Sqlite.Schema;

/// <summary>
/// Tracking table for recurring (cron) message schedules. See the PostgreSQL twin for the full
/// rationale — one row per registered schedule, bookkeeping beside the inbox and never a delivery
/// path, provisioned only on the Main store behind the
/// <see cref="DurabilitySettings.EnableRecurringMessages" /> opt-in.
/// </summary>
internal class RecurringMessagesTable : Table
{
    public RecurringMessagesTable(string schemaName) : base(
        new SqliteObjectName(TablePrefixing.Apply(schemaName, DatabaseConstants.RecurringMessagesTableName)))
    {
        // GH-3943: a SQLite "schema" is a table-name prefix, not a namespace, hence TablePrefixing
        // above. DateTimeOffsets ride as TEXT and the pause flag as INTEGER 0/1, matching how
        // Microsoft.Data.Sqlite binds and reads those CLR types on every other Wolverine table.
        AddColumn(DatabaseConstants.ScheduleName, "TEXT").NotNull().AsPrimaryKey();
        AddColumn(DatabaseConstants.CronExpression, "TEXT").NotNull();
        AddColumn(DatabaseConstants.EnvelopeIds, "TEXT").NotNull();
        AddColumn(DatabaseConstants.DeduplicationId, "TEXT").AllowNulls();
        AddColumn(DatabaseConstants.NextOccurrence, "TEXT").AllowNulls();
        AddColumn(DatabaseConstants.Paused, "INTEGER").NotNull();
        AddColumn(DatabaseConstants.PausedAt, "TEXT").AllowNulls();
        AddColumn(DatabaseConstants.LastUpdated, "TEXT").NotNull();
    }
}
