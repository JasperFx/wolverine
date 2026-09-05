using Weasel.Core;
using Weasel.MySql.Tables;
using Wolverine.RDBMS;

namespace Wolverine.MySql.Schema;

/// <summary>
/// Tracking table for recurring (cron) message schedules. See the PostgreSQL twin for the full
/// rationale — one row per registered schedule, bookkeeping beside the inbox and never a delivery
/// path, provisioned only on the Main store behind the
/// <see cref="DurabilitySettings.EnableRecurringMessages" /> opt-in.
/// </summary>
internal class RecurringMessagesTable : Table
{
    public RecurringMessagesTable(string schemaName) : base(
        new DbObjectName(schemaName, DatabaseConstants.RecurringMessagesTableName))
    {
        // 250 chars stays inside InnoDB's 3072-byte index key limit even at utf8mb4's 4 bytes per
        // character, so this is a legal primary key without a prefix length.
        AddColumn(DatabaseConstants.ScheduleName, "varchar(250)").NotNull().AsPrimaryKey();
        AddColumn(DatabaseConstants.CronExpression, "varchar(250)").NotNull();
        AddColumn(DatabaseConstants.EnvelopeIds, "varchar(2000)").NotNull();
        AddColumn(DatabaseConstants.DeduplicationId, "varchar(250)").AllowNulls();
        AddColumn<DateTimeOffset>(DatabaseConstants.NextOccurrence).AllowNulls();
        AddColumn<bool>(DatabaseConstants.Paused).NotNull();
        AddColumn<DateTimeOffset>(DatabaseConstants.PausedAt).AllowNulls();
        AddColumn<DateTimeOffset>(DatabaseConstants.LastUpdated).NotNull();
    }
}
