using Weasel.Core;
using Weasel.MySql.Tables;
using Wolverine.RDBMS;

namespace Wolverine.MySql.Schema;

/// <summary>GH-4742. Claims for <c>[DeduplicatedWithResponse]</c> HTTP endpoints. See the PostgreSQL twin.</summary>
internal class DeduplicatedResponsesTable : Table
{
    public DeduplicatedResponsesTable(string schemaName) : base(
        new DbObjectName(schemaName, DatabaseConstants.DeduplicatedResponsesTableName))
    {
        AddColumn(DatabaseConstants.DeduplicationId, "varchar(250)").NotNull().AsPrimaryKey();
        AddColumn<DateTimeOffset>(DatabaseConstants.Expires).NotNull();
        AddColumn(DatabaseConstants.Fingerprint, "varchar(64)").NotNull();
        AddColumn(DatabaseConstants.ResponseStatusCode, "int");

        // longtext: text stops at 64KB.
        AddColumn(DatabaseConstants.ResponseBody, "longtext");
        AddColumn(DatabaseConstants.ResponseLocation, "text");

        Indexes.Add(new IndexDefinition($"idx_{DatabaseConstants.DeduplicatedResponsesTableName}_expires")
        {
            Columns = [DatabaseConstants.Expires]
        });
    }
}
