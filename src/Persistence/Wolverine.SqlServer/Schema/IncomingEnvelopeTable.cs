using System;
using Weasel.Core;
using Weasel.SqlServer.Tables;
using Wolverine.RDBMS;

namespace Wolverine.SqlServer.Schema;

internal class IncomingEnvelopeTable : Table
{
    public IncomingEnvelopeTable(string schemaName) : base(
        new DbObjectName(schemaName, DatabaseConstants.IncomingTable))
    {
        AddColumn<Guid>(DatabaseConstants.Id).AsPrimaryKey();
        AddColumn("status", "varchar(25)").NotNull();
        AddColumn<int>(DatabaseConstants.OwnerId).NotNull();
        AddColumn<DateTimeOffset>(DatabaseConstants.ExecutionTime).DefaultValueByExpression("NULL");
        AddColumn<int>(DatabaseConstants.Attempts).DefaultValue(0);
        AddColumn(DatabaseConstants.Body, "varbinary(max)").NotNull();
        
        AddColumn(DatabaseConstants.MessageType, "varchar(250)").NotNull();
        AddColumn<string>(DatabaseConstants.ReceivedAt);
        AddColumn<DateTimeOffset>(DatabaseConstants.KeepUntil);
    }
}