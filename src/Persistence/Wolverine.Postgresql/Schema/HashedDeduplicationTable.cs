using Weasel.Core;
using Weasel.Postgresql;
using Weasel.Postgresql.Tables;
using Wolverine.RDBMS;

namespace Wolverine.Postgresql.Schema;

/// <summary>
/// GH-4757. Storage for logical message deduplication claims under
/// <see cref="MessageDeduplicationMode.CompareByHash" />. Provisioned instead of
/// <see cref="DeduplicationTable" />, never alongside it.
///
/// <para>
/// The primary key on <c>deduplication_hash</c> IS the guarantee, exactly as the key on
/// <c>deduplication_id</c> is on the original table — claiming is an INSERT that either succeeds or
/// trips this constraint, which is the only check that holds across concurrent nodes.
/// </para>
///
/// <para>
/// This exists as its OWN table rather than as a reshaping of <see cref="DeduplicationTable" />
/// because moving a primary key is the one migration Weasel cannot express under
/// <c>CreateOrUpdate</c>. The cost of reshaping was a startup failure or a dropped set of live claims;
/// the cost of a second table is a table left behind, whose rows expire on their own. See
/// <see cref="DatabaseConstants.HashedDeduplicationTableName" />.
/// </para>
/// </summary>
internal class HashedDeduplicationTable : Table
{
    public HashedDeduplicationTable(string schemaName) : base(
        new DbObjectName(schemaName, DatabaseConstants.HashedDeduplicationTableName))
    {
        // bytea, not a hex string: a binary column has no collation to apply, which is the entire
        // point of this mode. Hex would also work here, but only because its alphabet happens to have
        // no case variants -- a property the next person to change the encoding would not know to
        // preserve.
        AddColumn(DatabaseConstants.DeduplicationHash, "bytea").NotNull().AsPrimaryKey();

        // The readable id, kept for diagnosis -- "which key is this stuck claim?" -- since a SHA-256
        // cannot be reversed. Deliberately NOT unique: a unique constraint here would be compared under
        // the database's collation and would refuse 'abc' after 'Abc' all over again, which is the
        // defect GH-4757 reports.
        AddColumn(DatabaseConstants.DeduplicationId, "varchar(250)").NotNull();

        AddColumn<DateTimeOffset>(DatabaseConstants.Expires).NotNull();

        // The reaper's only predicate. Without it, every cleanup cycle is a full scan of a table whose
        // whole purpose is to be large.
        Indexes.Add(new IndexDefinition(
            PostgresqlIdentifier.Shorten($"idx_{DatabaseConstants.HashedDeduplicationTableName}_expires"))
        {
            Columns = [DatabaseConstants.Expires]
        });
    }
}
