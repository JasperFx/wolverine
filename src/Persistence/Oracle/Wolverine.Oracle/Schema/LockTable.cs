using Weasel.Oracle;
using Weasel.Oracle.Tables;

namespace Wolverine.Oracle.Schema;

internal class LockTable : Table
{
    public const string TableName = "WOLVERINE_LOCKS";

    public LockTable(string schemaName) : base(
        new OracleObjectName(schemaName.ToUpperInvariant(), TableName))
    {
        AddColumn<int>("lock_id").AsPrimaryKey();
    }
}
