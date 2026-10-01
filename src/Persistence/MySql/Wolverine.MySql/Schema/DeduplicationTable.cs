using Weasel.Core;
using Weasel.MySql.Tables;
using Wolverine.RDBMS;

namespace Wolverine.MySql.Schema;

/// <summary>
/// GH-4180. Storage for logical message deduplication claims. See the PostgreSQL twin for why this
/// is its own table rather than a column on the incoming envelope table, and for what the two
/// <see cref="MessageDeduplicationMode" /> shapes are.
/// </summary>
/// <remarks>
/// GH-4757. MySQL is the engine the hash mode matters most on: its default collation is both
/// case-INsensitive and accent-insensitive, so under
/// <see cref="MessageDeduplicationMode.CompareByString" /> the primary key below treats <c>Abc</c> and
/// <c>abc</c> as one claim, and <c>José</c> and <c>Jose</c> as one claim too. <c>binary(32)</c> has no
/// collation at all.
/// </remarks>
internal class DeduplicationTable : Table
{
    public DeduplicationTable(string schemaName, MessageDeduplicationMode mode) : base(
        new DbObjectName(schemaName, DatabaseConstants.DeduplicationTableName))
    {
        if (mode == MessageDeduplicationMode.CompareByHash)
        {
            // 32 bytes, nowhere near InnoDB's 3072-byte index key limit, so this is a legal primary key
            // without a prefix length -- and unlike the varchar below it does not depend on the
            // character set to stay that way.
            AddColumn(DatabaseConstants.DeduplicationHash, "binary(32)").NotNull().AsPrimaryKey();

            // Diagnosis only, and therefore NOT unique. See the PostgreSQL twin.
            AddColumn(DatabaseConstants.DeduplicationId, "varchar(250)").NotNull();
        }
        else
        {
            // 250 chars stays inside InnoDB's 3072-byte index key limit even at utf8mb4's 4 bytes per
            // character, so this is a legal primary key without a prefix length.
            AddColumn(DatabaseConstants.DeduplicationId, "varchar(250)").NotNull().AsPrimaryKey();
        }

        AddColumn<DateTimeOffset>(DatabaseConstants.Expires).NotNull();

        Indexes.Add(new IndexDefinition($"idx_{DatabaseConstants.DeduplicationTableName}_expires")
        {
            Columns = [DatabaseConstants.Expires]
        });
    }
}
