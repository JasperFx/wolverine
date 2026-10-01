using Weasel.Core;
using Weasel.SqlServer.Tables;
using Wolverine.RDBMS;

namespace Wolverine.SqlServer.Schema;

/// <summary>GH-4742. Claims for <c>[DeduplicatedWithResponse]</c> HTTP endpoints. See the PostgreSQL twin.</summary>
internal class DeduplicatedResponsesTable : Table
{
    public DeduplicatedResponsesTable(string schemaName) : base(
        new DbObjectName(schemaName, DatabaseConstants.DeduplicatedResponsesTableName))
    {
        AddColumn(DatabaseConstants.DeduplicationId, "varchar(250)").NotNull().AsPrimaryKey();
        AddColumn<DateTimeOffset>(DatabaseConstants.Expires).NotNull();
        AddColumn(DatabaseConstants.Fingerprint, "varchar(64)").NotNull();
        AddColumn(DatabaseConstants.ClaimToken, "varchar(36)").NotNull();
        AddColumn(DatabaseConstants.ResponseStatusCode, "int");
        AddColumn(DatabaseConstants.ResponseBody, "nvarchar(max)");
        AddColumn(DatabaseConstants.ResponseLocation, "nvarchar(max)");

        Indexes.Add(new IndexDefinition($"idx_{DatabaseConstants.DeduplicatedResponsesTableName}_expires")
        {
            Columns = [DatabaseConstants.Expires]
        });
    }
}
