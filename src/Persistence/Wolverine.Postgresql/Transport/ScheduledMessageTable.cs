using Weasel.Core;
using Weasel.Postgresql;
using Weasel.Postgresql.Tables;
using Wolverine.RDBMS;

namespace Wolverine.Postgresql.Transport;

internal class ScheduledMessageTable : Table
{
    public ScheduledMessageTable(PostgresqlTransport settings, string tableName) : base(
        new DbObjectName(settings.TransportSchemaName, tableName))
    {
        AddColumn<Guid>(DatabaseConstants.Id).AsPrimaryKey();
        AddColumn(DatabaseConstants.Body, "bytea").NotNull();
        AddColumn<string>(DatabaseConstants.MessageType).NotNull();
        AddColumn<DateTimeOffset>(DatabaseConstants.ExecutionTime).NotNull();
        AddColumn<DateTimeOffset>(DatabaseConstants.KeepUntil);
        AddColumn<DateTimeOffset>("timestamp").DefaultValueByExpression("((now() at time zone 'utc'))");

        // Definitely want to index the execution time. Far more reads than writes. We think.
        //
        // GH-2942: the constructed index name `idx_{tableName}_execution_time` can exceed
        // PostgreSQL's NAMEDATALEN (default 63 chars) even when `tableName` itself is within
        // limits. Use PostgresqlIdentifier.Shorten() to predictably stay under the limit with a
        // deterministic hash suffix so the in-memory model matches what PostgreSQL stores; that
        // keeps the schema-diff clean rather than failing with "Missing known broker resources".
        var indexName = PostgresqlIdentifier.Shorten($"idx_{tableName}_execution_time");
        Indexes.Add(new IndexDefinition(indexName)
        {
            Columns = [DatabaseConstants.ExecutionTime]
        });
    }
}