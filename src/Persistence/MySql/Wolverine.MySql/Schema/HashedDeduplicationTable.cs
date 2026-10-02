using Weasel.Core;
using Weasel.MySql.Tables;
using Wolverine.RDBMS;

namespace Wolverine.MySql.Schema;

/// <summary>
/// GH-4757. Storage for logical message deduplication claims under
/// <see cref="MessageDeduplicationMode.CompareByHash" />. See the PostgreSQL twin for why this is its
/// own table rather than a reshaping of <see cref="DeduplicationTable" />.
/// </summary>
/// <remarks>
/// MySQL is the engine this mode matters most on: its default collation is both case-INsensitive and
/// accent-insensitive, so the key on <see cref="DeduplicationTable" /> treats <c>Abc</c> and <c>abc</c>
/// as one claim, and <c>José</c> and <c>Jose</c> as one claim too. <c>binary(32)</c> has no collation at
/// all.
/// </remarks>
internal class HashedDeduplicationTable : Table
{
    public HashedDeduplicationTable(string schemaName) : base(
        new DbObjectName(schemaName, DatabaseConstants.HashedDeduplicationTableName))
    {
        // 32 bytes, nowhere near InnoDB's 3072-byte index key limit, so this is a legal primary key
        // without a prefix length -- and unlike a varchar it does not depend on the character set to
        // stay that way.
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
