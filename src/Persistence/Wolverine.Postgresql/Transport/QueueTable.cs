using Weasel.Core;
using Weasel.Postgresql;
using Weasel.Postgresql.Tables;
using Wolverine.RDBMS;

namespace Wolverine.Postgresql.Transport;

internal class QueueTable : Table
{
    internal const string QueueOrderColumn = "queue_order";

    public QueueTable(PostgresqlTransport parent, string tableName) : base(
        new DbObjectName(parent.TransportSchemaName, tableName))
    {
        AddOnlyMigrations = true;

        AddColumn<Guid>(DatabaseConstants.Id).AsPrimaryKey();
        AddColumn(DatabaseConstants.Body, "bytea").NotNull();
        AddColumn<string>(DatabaseConstants.MessageType).NotNull();
        AddColumn<DateTimeOffset>(DatabaseConstants.KeepUntil);
        AddColumn<DateTimeOffset>("timestamp").DefaultValueByExpression("now()");

        // Keep the released schema as the default. The persistent sequence is an opt-in schema
        // change because PostgreSQL's now() is fixed for a transaction and promoted rows can tie.
        if (parent.EnableDeterministicQueueOrdering)
        {
            AddColumn(new QueueOrderingColumn());
        }

        var indexName = PostgresqlIdentifier.Shorten($"idx_{tableName}_timestamp");
        Indexes.Add(new IndexDefinition(indexName) { Columns = ["timestamp"] });

        if (parent.EnableDeterministicQueueOrdering)
        {
            // The separate name makes enabling this additive and keeps the legacy index definition
            // stable for applications that have not opted in.
            var orderedIndexName = PostgresqlIdentifier.Shorten($"idx_{tableName}_timestamp_queue_order");
            Indexes.Add(new IndexDefinition(orderedIndexName)
            {
                Columns = ["timestamp", QueueOrderColumn]
            });
        }
    }
}
