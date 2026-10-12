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
        AddColumn<Guid>(DatabaseConstants.Id).AsPrimaryKey();
        AddColumn(DatabaseConstants.Body, "bytea").NotNull();
        AddColumn<string>(DatabaseConstants.MessageType).NotNull();
        AddColumn<DateTimeOffset>(DatabaseConstants.KeepUntil);
        AddColumn<DateTimeOffset>("timestamp").DefaultValueByExpression("now()");

        // PostgreSQL's now() is fixed for a transaction, so messages promoted by one INSERT ... SELECT can
        // share a timestamp. Keep a persistent sequence as the tie-breaker for FIFO dequeue ordering.
        AddColumn<long>(QueueOrderColumn).BigAutoIncrement().NotNull();

        // The dequeue path orders by timestamp and queue_order (TOP/LIMIT n ... ORDER BY ...) on every poll.
        // Without this index the ordered LIMIT has to scan + sort the whole queue table; a btree on
        // both columns turns it into an ordered index scan. See GH perf review. The index name is run
        // through Shorten() to stay under PostgreSQL's 63-char NAMEDATALEN limit (see GH-2942).
        var indexName = PostgresqlIdentifier.Shorten($"idx_{tableName}_timestamp");
        Indexes.Add(new IndexDefinition(indexName)
        {
            Columns = ["timestamp", QueueOrderColumn]
        });
    }
}
