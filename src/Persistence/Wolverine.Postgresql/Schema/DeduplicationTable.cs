using Weasel.Core;
using Weasel.Postgresql;
using Weasel.Postgresql.Tables;
using Wolverine.RDBMS;

namespace Wolverine.Postgresql.Schema;

/// <summary>
/// GH-4180. Storage for logical message deduplication claims. Provisioned only when
/// <see cref="DurabilitySettings.EnableMessageDeduplication" /> is set.
///
/// <para>
/// Two columns and nothing else. The primary key on <c>deduplication_id</c> IS the guarantee —
/// claiming is an INSERT that either succeeds or trips this constraint, which is the only check
/// that holds across concurrent nodes.
/// </para>
/// </summary>
internal class DeduplicationTable : Table
{
    public DeduplicationTable(string schemaName) : base(
        new DbObjectName(schemaName, DatabaseConstants.DeduplicationTableName))
    {
        // 250 characters matches the message_type / received_at convention on the envelope tables, and
        // stays well inside every engine's maximum index key width. A logical id is meant to be legible
        // in the database when someone is working out why a job did not fire -- "{scheduleId}|{occurrenceUtc:O}"
        // and its like -- not to carry a payload.
        AddColumn(DatabaseConstants.DeduplicationId, "varchar(250)").NotNull().AsPrimaryKey();
        AddColumn<DateTimeOffset>(DatabaseConstants.Expires).NotNull();

        // The reaper's only predicate. Without it, every cleanup cycle is a full scan of a table whose
        // whole purpose is to be large.
        Indexes.Add(new IndexDefinition(
            PostgresqlIdentifier.Shorten($"idx_{DatabaseConstants.DeduplicationTableName}_expires"))
        {
            Columns = [DatabaseConstants.Expires]
        });
    }
}
