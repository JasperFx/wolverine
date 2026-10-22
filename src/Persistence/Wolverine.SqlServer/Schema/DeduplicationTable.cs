using Weasel.Core;
using Weasel.SqlServer.Tables;
using Wolverine.RDBMS;

namespace Wolverine.SqlServer.Schema;

/// <summary>
/// GH-4180. Storage for logical message deduplication claims. See the PostgreSQL twin for why this
/// is its own table rather than a column on the incoming envelope table.
/// </summary>
internal class DeduplicationTable : Table
{
    public DeduplicationTable(string schemaName) : base(
        new DbObjectName(schemaName, DatabaseConstants.DeduplicationTableName))
    {
        AddColumn(DatabaseConstants.DeduplicationId, "varchar(250)").NotNull().AsPrimaryKey();
        AddColumn<DateTimeOffset>(DatabaseConstants.Expires).NotNull();

        // D1. The envelope that took the claim, so the copy inbox recovery brings back after a node died
        // mid-handler can take its own stranded claim over. Nullable, so adding it is a plain additive migration
        // and an existing row keeps the original refuse-everything behaviour.
        AddColumn<Guid>(DatabaseConstants.DeduplicationEnvelopeId);

        Indexes.Add(new IndexDefinition($"idx_{DatabaseConstants.DeduplicationTableName}_expires")
        {
            Columns = [DatabaseConstants.Expires]
        });
    }
}
