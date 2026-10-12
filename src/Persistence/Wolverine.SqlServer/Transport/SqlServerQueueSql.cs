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
    ///     <c>timestamp</c> with <c>queue_order</c> as the tie-breaker when that column is enabled.
    /// </summary>
    internal static string[] ReadyOrdering(SqlServerQueue queue)
    {
        if (queue.OptimizeThroughput) return [Seq];

        return queue.UsesQueueOrderColumn ? ["timestamp", QueueTable.QueueOrderColumn] : ["timestamp"];
    }

    /// <summary>
    ///     Promotion order for the scheduled table: <c>execution_time</c>, with the table's identity
    ///     (<c>seq</c> or the opt-in <c>queue_order</c>) as the tie-breaker when there is one.
    /// </summary>
    internal static string[] ScheduledOrdering(SqlServerQueue queue)
    {
        if (queue.OptimizeThroughput) return [DatabaseConstants.ExecutionTime, Seq];

        return queue.UsesQueueOrderColumn
            ? [DatabaseConstants.ExecutionTime, QueueTable.QueueOrderColumn]
            : [DatabaseConstants.ExecutionTime];
    }

    internal static string TryPopDirectly(SqlServerQueue queue)
    {
        var table = queue.QueueTable.Identifier;
        var ordering = ReadyOrdering(queue);

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

    internal static string TryPopToInbox(SqlServerQueue queue, Uri receivedAt)
    {
        var table = queue.QueueTable.Identifier;
        var incoming = $"{queue.Parent.MessageStorageSchemaName}.{DatabaseConstants.IncomingTable}";
        var ordering = ReadyOrdering(queue);

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

INSERT INTO {incoming}
(id, status, owner_id, body, message_type, received_at, keep_until)
 SELECT id, 'Incoming', @node, body, message_type, '{receivedAt}', keep_until FROM @popped;
select body from @popped ORDER BY {columns(ordering)};

IF (@NOCOUNT = 'ON') SET NOCOUNT ON;
IF (@NOCOUNT = 'OFF') SET NOCOUNT OFF;";
    }

    /// <summary>
    ///     GH-4334: a table variable instead of a per-call #temp_move, and a TOP bound so a large
    ///     due backlog is promoted in bounded batches rather than one enormous lock-holding
    ///     transaction.
    /// </summary>
    internal static string MoveScheduledToReadyQueue(SqlServerQueue queue, int batchSize)
    {
        var queueTable = queue.QueueTable.Identifier;
        var scheduledTable = queue.ScheduledTable.Identifier;
        var ordering = ScheduledOrdering(queue);

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
