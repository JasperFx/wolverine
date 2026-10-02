using Weasel.Core;
using Weasel.SqlServer.Tables;
using Wolverine.RDBMS;

namespace Wolverine.SqlServer.Schema;

/// <summary>
/// GH-4757. Storage for logical message deduplication claims under
/// <see cref="MessageDeduplicationMode.CompareByHash" />. See the PostgreSQL twin for why this is its
/// own table rather than a reshaping of <see cref="DeduplicationTable" />.
/// </summary>
/// <remarks>
/// SQL Server is one of the two engines this mode exists for. Its default collation is
/// case-insensitive, so the key on <see cref="DeduplicationTable" /> treats <c>Abc</c> and <c>abc</c> as
/// one claim; <c>varchar</c> additionally substitutes <c>?</c> for any character outside the code page,
/// which collapses distinct ids outright. <c>binary(32)</c> has neither a collation nor a code page.
/// </remarks>
internal class HashedDeduplicationTable : Table
{
    public HashedDeduplicationTable(string schemaName) : base(
        new DbObjectName(schemaName, DatabaseConstants.HashedDeduplicationTableName))
    {
        // Fixed-width binary(32), not varbinary: a SHA-256 is always exactly 32 bytes, and a fixed
        // width keeps the clustered key narrow.
        AddColumn(DatabaseConstants.DeduplicationHash, "binary(32)").NotNull().AsPrimaryKey();

        // Diagnosis only, and therefore NOT unique. See the PostgreSQL twin.
        AddColumn(DatabaseConstants.DeduplicationId, "varchar(250)").NotNull();

        AddColumn<DateTimeOffset>(DatabaseConstants.Expires).NotNull();

        Indexes.Add(new IndexDefinition($"idx_{DatabaseConstants.HashedDeduplicationTableName}_expires")
        {
            Columns = [DatabaseConstants.Expires]
        });
    }
}
