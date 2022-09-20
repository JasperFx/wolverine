using Wolverine.RDBMS;
using Weasel.Core;
using Weasel.SqlServer.Tables;

namespace Wolverine.SqlServer.Schema;

internal class EnvelopeIdTable : TableType
{
    public EnvelopeIdTable(string schemaName) : base(new DbObjectName(schemaName, "EnvelopeIdList"))
    {
        AddColumn(DatabaseConstants.Id, "UNIQUEIDENTIFIER");
    }
}
