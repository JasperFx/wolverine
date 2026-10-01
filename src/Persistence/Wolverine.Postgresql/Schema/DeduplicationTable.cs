using Weasel.Core;
using Weasel.Postgresql;
using Weasel.Postgresql.Tables;
using Wolverine.RDBMS;

namespace Wolverine.Postgresql.Schema;

/// <summary>
/// GH-4180. Storage for logical message deduplication claims. Provisioned only when
/// <see cref="DurabilitySettings.MessageDeduplicationMode" /> is anything but
/// <see cref="MessageDeduplicationMode.None" />.
///
/// <para>
/// Whichever shape, the primary key IS the guarantee — claiming is an INSERT that either succeeds or
/// trips this constraint, which is the only check that holds across concurrent nodes.
/// </para>
///
/// <para>
/// GH-4757. Which column carries that key depends on the mode.
/// <see cref="MessageDeduplicationMode.CompareByString" /> keeps the original two-column shape, with the
/// readable id as the key and therefore compared under the column's collation.
/// <see cref="MessageDeduplicationMode.CompareByHash" /> moves the key onto a binary
/// <c>deduplication_hash</c> and demotes <c>deduplication_id</c> to a plain, non-unique column — which
/// it must be: leave a unique constraint on it and a case-insensitive collation still refuses
/// <c>abc</c> after <c>Abc</c>, and nothing has been fixed.
/// </para>
/// </summary>
internal class DeduplicationTable : Table
{
    public DeduplicationTable(string schemaName, MessageDeduplicationMode mode) : base(
        new DbObjectName(schemaName, DatabaseConstants.DeduplicationTableName))
    {
        if (mode == MessageDeduplicationMode.CompareByHash)
        {
            // bytea rather than a hex string: text of any kind is compared under the database's
            // collation, which is the defect being fixed.
            AddColumn(DatabaseConstants.DeduplicationHash, "bytea").NotNull().AsPrimaryKey();

            // Kept for diagnosis only -- "which key is this stuck claim?" -- and therefore NOT unique.
            AddColumn(DatabaseConstants.DeduplicationId, "varchar(250)").NotNull();
        }
        else
        {
            // 250 characters matches the message_type / received_at convention on the envelope tables, and
            // stays well inside every engine's maximum index key width. A logical id is meant to be legible
            // in the database when someone is working out why a job did not fire -- "{scheduleId}|{occurrenceUtc:O}"
            // and its like -- not to carry a payload.
            AddColumn(DatabaseConstants.DeduplicationId, "varchar(250)").NotNull().AsPrimaryKey();
        }

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
