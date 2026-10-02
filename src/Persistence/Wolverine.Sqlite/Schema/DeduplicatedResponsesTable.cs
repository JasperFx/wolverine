using Weasel.Sqlite;
using Weasel.Sqlite.Tables;
using Wolverine.RDBMS;

namespace Wolverine.Sqlite.Schema;

/// <summary>GH-4742. Claims for <c>[DeduplicatedWithResponse]</c> HTTP endpoints. See the PostgreSQL twin.</summary>
internal class DeduplicatedResponsesTable : Table
{
    public DeduplicatedResponsesTable(string schemaName) : base(
        new SqliteObjectName(TablePrefixing.Apply(schemaName, DatabaseConstants.DeduplicatedResponsesTableName)))
    {
        AddColumn(DatabaseConstants.DeduplicationId, "TEXT").NotNull().AsPrimaryKey();
        AddColumn(DatabaseConstants.Expires, "TEXT").NotNull();
        AddColumn(DatabaseConstants.Fingerprint, "TEXT").NotNull();
        AddColumn(DatabaseConstants.ClaimToken, "TEXT").NotNull();
        AddColumn(DatabaseConstants.ResponseStatusCode, "INTEGER");
        AddColumn(DatabaseConstants.ResponseBody, "TEXT");
        AddColumn(DatabaseConstants.ResponseLocation, "TEXT");

        // Index names share one SQLite namespace, hence the prefixed table name.
        var tableName = TablePrefixing.Apply(schemaName, DatabaseConstants.DeduplicatedResponsesTableName);
        Indexes.Add(new IndexDefinition($"idx_{tableName}_expires")
        {
            Columns = [DatabaseConstants.Expires]
        });
    }
}
