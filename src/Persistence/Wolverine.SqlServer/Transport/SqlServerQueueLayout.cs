namespace Wolverine.SqlServer.Transport;

/// <summary>
///     GH-5192. What one queue table in one database actually has: the high-throughput layout's <c>seq</c>
///     identity, or the classic layout with or without the <c>queue_order</c> tie-breaker.
/// </summary>
internal readonly record struct SqlServerTableLayout(bool Seq, bool QueueOrder);

/// <summary>
///     GH-5192. The layouts of a queue's ready and scheduled tables in one database. They can differ: an existing
///     table is adopted as it stands while its missing partner is created on the configured layout.
/// </summary>
internal readonly record struct SqlServerQueueLayout(SqlServerTableLayout Ready, SqlServerTableLayout Scheduled);
