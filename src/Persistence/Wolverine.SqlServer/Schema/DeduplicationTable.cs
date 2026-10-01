using Weasel.Core;
using Weasel.SqlServer.Tables;
using Wolverine.RDBMS;

namespace Wolverine.SqlServer.Schema;

/// <summary>
/// GH-4180. Storage for logical message deduplication claims. See the PostgreSQL twin for why this
/// is its own table rather than a column on the incoming envelope table, and for what the two
/// <see cref="MessageDeduplicationMode" /> shapes are.
/// </summary>
/// <remarks>
/// GH-4757. SQL Server is one of the two engines the hash mode exists for. Its default collation is
/// case-insensitive, so under <see cref="MessageDeduplicationMode.CompareByString" /> the primary key
/// below treats <c>Abc</c> and <c>abc</c> as one claim; <c>varchar</c> additionally substitutes <c>?</c>
/// for any character outside the code page, which collapses distinct ids outright.
/// <c>binary(32)</c> has no collation and no code page.
/// </remarks>
internal class DeduplicationTable : Table
{
    public DeduplicationTable(string schemaName, MessageDeduplicationMode mode) : base(
        new DbObjectName(schemaName, DatabaseConstants.DeduplicationTableName))
    {
        if (mode == MessageDeduplicationMode.CompareByHash)
        {
            // Fixed-width binary(32), not varbinary: a SHA-256 is always exactly 32 bytes, and a fixed
            // width keeps the clustered key narrow.
            AddColumn(DatabaseConstants.DeduplicationHash, "binary(32)").NotNull().AsPrimaryKey();

            // Diagnosis only, and therefore NOT unique. See the PostgreSQL twin.
            AddColumn(DatabaseConstants.DeduplicationId, "varchar(250)").NotNull();
        }
        else
        {
            AddColumn(DatabaseConstants.DeduplicationId, "varchar(250)").NotNull().AsPrimaryKey();
        }

        AddColumn<DateTimeOffset>(DatabaseConstants.Expires).NotNull();

        Indexes.Add(new IndexDefinition($"idx_{DatabaseConstants.DeduplicationTableName}_expires")
        {
            Columns = [DatabaseConstants.Expires]
        });
    }
}
