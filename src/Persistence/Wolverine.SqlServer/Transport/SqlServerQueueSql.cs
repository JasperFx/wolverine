using JasperFx.Core;
using Wolverine.RDBMS;

namespace Wolverine.SqlServer.Transport;

/// <summary>
///     The dequeue and scheduled-promotion SQL for a <see cref="SqlServerQueue" />. Shared by
///     <see cref="SqlServerQueueListener" /> and the queue's own test helper methods so the two can
///     never disagree about ordering.
/// </summary>
/// <remarks>
///     GH-4862. <c>DELETE ... OUTPUT</c> returns rows in no guaranteed order, whatever the
///     <c>ORDER BY</c> inside the CTE that picked them. Every statement here therefore captures the
///     ordering columns alongside the deleted rows and reapplies the order when it reads them back:
///     the pops hand their batch over in queue order, and the promotion inserts into the ready table
///     in <c>execution_time</c> order, which SQL Server guarantees is also the order the ready table's
///     identity values (<c>seq</c> or <c>queue_order</c>) are assigned in.
/// </remarks>
internal static class SqlServerQueueSql
{
    private const string Seq = "seq";

    /// <summary>
    ///     Dequeue order for the ready table: <c>seq</c> under the high-throughput layout, otherwise
    ///     <c>timestamp</c> with <c>queue_order</c> as the tie-breaker when the table has that column.
    /// </summary>
    internal static string[] ReadyOrdering(SqlServerTableLayout layout)
    {
        if (layout.Seq) return [Seq];

        return layout.QueueOrder ? ["timestamp", QueueTable.QueueOrderColumn] : ["timestamp"];
    }

    /// <summary>
    ///     Promotion order for the scheduled table: <c>execution_time</c>, with the table's identity
    ///     (<c>seq</c> or <c>queue_order</c>) as the tie-breaker when there is one.
    /// </summary>
    internal static string[] ScheduledOrdering(SqlServerTableLayout layout)
    {
        if (layout.Seq) return [DatabaseConstants.ExecutionTime, Seq];

        return layout.QueueOrder
            ? [DatabaseConstants.ExecutionTime, QueueTable.QueueOrderColumn]
            : [DatabaseConstants.ExecutionTime];
    }

    internal static string TryPopDirectly(SqlServerQueue queue) => TryPopDirectly(queue, queue.ConfiguredLayout);

    internal static string TryPopToInbox(SqlServerQueue queue, Uri receivedAt, MessageIdentity identity) =>
        TryPopToInbox(queue, queue.ConfiguredLayout, receivedAt, identity);

    internal static string MoveScheduledToReadyQueue(SqlServerQueue queue, int batchSize) =>
        MoveScheduledToReadyQueue(queue, queue.ConfiguredLayout, batchSize);

    internal static string TryPopDirectly(SqlServerQueue queue, SqlServerQueueLayout layout)
    {
        var table = queue.QueueTable.Identifier;
        var ordering = ReadyOrdering(layout.Ready);

        return $@"
DECLARE @NOCOUNT VARCHAR(3) = 'OFF';
IF ( (512 & @@OPTIONS) = 512 ) SET @NOCOUNT = 'ON';
SET NOCOUNT ON;

DECLARE @popped TABLE ({DatabaseConstants.Body} varbinary(max), {declarations(ordering)});

WITH message AS (
    SELECT TOP(@count) {DatabaseConstants.Body}, {columns(ordering)}
    FROM {table} WITH (UPDLOCK, READPAST, ROWLOCK)
    ORDER BY {columns(ordering, table.ToString())})
DELETE FROM message
OUTPUT deleted.{DatabaseConstants.Body}, {columns(ordering, "deleted")} INTO @popped;

SELECT {DatabaseConstants.Body} FROM @popped ORDER BY {columns(ordering)};

IF (@NOCOUNT = 'ON') SET NOCOUNT ON;
IF (@NOCOUNT = 'OFF') SET NOCOUNT OFF;";
    }

    internal static string TryPopToInbox(SqlServerQueue queue, SqlServerQueueLayout layout, Uri receivedAt,
        MessageIdentity identity)
    {
        var table = queue.QueueTable.Identifier;
        var incoming = $"{queue.Parent.MessageStorageSchemaName}.{DatabaseConstants.IncomingTable}";
        var ordering = ReadyOrdering(layout.Ready);

        return $@"
DECLARE @NOCOUNT VARCHAR(3) = 'OFF';
IF ( (512 & @@OPTIONS) = 512 ) SET @NOCOUNT = 'ON';
SET NOCOUNT ON;

delete FROM {table} WITH (UPDLOCK, READPAST, ROWLOCK) where id in (select id from {incoming} where {DatabaseConstants.ReceivedAt} = '{receivedAt}');
-- GH-4334: a declared table variable instead of SELECT ... INTO #temp_pop. The temp table was
-- created and dropped on every poll of every queue: tempdb allocation churn plus a statement
-- recompile each time, because a SELECT INTO builds a fresh schema the optimizer has never seen.
DECLARE @popped TABLE (id uniqueidentifier, body varbinary(max), message_type varchar(250), keep_until datetimeoffset, {declarations(ordering)});

WITH message AS (
    SELECT TOP(@count) id, body, message_type, keep_until, {columns(ordering)}
    FROM {table} WITH (UPDLOCK, READPAST, ROWLOCK)
    ORDER BY {columns(ordering, table.ToString())})
DELETE FROM message
OUTPUT deleted.id, deleted.body, deleted.message_type, deleted.keep_until, {columns(ordering, "deleted")} INTO @popped;

-- GH-5411: an envelope the inbox already holds (say, handled after being received at another address) is
-- dropped from the batch instead of failing the whole INSERT with a primary key violation, which rolled every
-- envelope of the batch back onto the queue on every poll. The probe matches the inbox's identity.
DECLARE @duplicates TABLE (id uniqueidentifier);
INSERT INTO @duplicates (id)
 SELECT p.id FROM @popped p WHERE EXISTS (SELECT 1 FROM {incoming} existing WHERE existing.id = p.id{(identity == MessageIdentity.IdAndDestination ? $" AND existing.{DatabaseConstants.ReceivedAt} = '{receivedAt}'" : "")});

INSERT INTO {incoming}
(id, status, owner_id, body, message_type, received_at, keep_until)
 SELECT id, 'Incoming', @node, body, message_type, '{receivedAt}', keep_until FROM @popped WHERE id NOT IN (SELECT id FROM @duplicates);
select p.id, p.body, CASE WHEN d.id IS NULL THEN CAST(0 AS bit) ELSE CAST(1 AS bit) END
 from @popped p LEFT JOIN @duplicates d ON d.id = p.id ORDER BY {columns(ordering, "p")};

IF (@NOCOUNT = 'ON') SET NOCOUNT ON;
IF (@NOCOUNT = 'OFF') SET NOCOUNT OFF;";
    }

    /// <summary>
    ///     GH-4334: a table variable instead of a per-call #temp_move, and a TOP bound so a large
    ///     due backlog is promoted in bounded batches rather than one enormous lock-holding
    ///     transaction.
    /// </summary>
    internal static string MoveScheduledToReadyQueue(SqlServerQueue queue, SqlServerQueueLayout layout, int batchSize)
    {
        var queueTable = queue.QueueTable.Identifier;
        var scheduledTable = queue.ScheduledTable.Identifier;
        var ordering = ScheduledOrdering(layout.Scheduled);

        return $@"
DECLARE @moved TABLE (id uniqueidentifier, body varbinary(max), message_type varchar(250), keep_until datetimeoffset, {declarations(ordering)});

WITH due AS (
    SELECT TOP({batchSize}) id, body, message_type, keep_until, {columns(ordering)}
    FROM {scheduledTable} WITH (UPDLOCK, READPAST, ROWLOCK)
    WHERE {DatabaseConstants.ExecutionTime} <= SYSDATETIMEOFFSET() AND ID NOT IN (select id from {queueTable})
    ORDER BY {columns(ordering, scheduledTable.ToString())})
DELETE FROM due
OUTPUT deleted.id, deleted.body, deleted.message_type, deleted.keep_until, {columns(ordering, "deleted")} INTO @moved;

INSERT INTO {queueTable}
(id, body, message_type, keep_until)
 SELECT id, body, message_type, keep_until FROM @moved ORDER BY {columns(ordering)};
select count(*) from @moved
";
    }

    private static string columns(string[] ordering, string? prefix = null)
    {
        return ordering.Select(x => prefix == null ? $"[{x}]" : $"{prefix}.[{x}]").Join(", ");
    }

    private static string declarations(string[] ordering)
    {
        return ordering.Select(x => $"[{x}] {sqlType(x)}").Join(", ");
    }

    private static string sqlType(string column)
    {
        return column is Seq or QueueTable.QueueOrderColumn ? "bigint" : "datetimeoffset";
    }
}
