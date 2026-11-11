using Weasel.SqlServer.Tables;

namespace Wolverine.SqlServer.Transport;

/// <summary>
///     GH-5192. The layout a queue's ready and scheduled tables are built with, and which parts of it came from a
///     7.0 default rather than an explicit setting.
/// </summary>
/// <remarks>
///     The 7.0 defaults (the <c>OptimizeQueueThroughput()</c> layout, and <c>queue_order</c> on the classic
///     layout) shape only the tables Wolverine creates. Moving an existing table to the other layout rebuilds it,
///     and adding an identity numbers every row; both hold an exclusive lock for as long as that takes, and two
///     applications that disagreed would rebuild the table back and forth. So an existing table is diffed against
///     the layout it already has, unless that part of the layout was set explicitly, which keeps 6.x's behaviour.
/// </remarks>
internal readonly record struct QueueTableShape(
    bool OptimizeThroughput,
    bool DeterministicOrdering,
    bool AdoptLayout,
    bool AdoptOrdering)
{
    internal const string SeqColumn = "seq";

    /// <summary>
    ///     Does the table carry the <c>queue_order</c> tie-breaker? Never on the high-throughput layout, whose
    ///     <c>seq</c> identity already orders it; SQL Server allows one identity column per table.
    /// </summary>
    public bool QueueOrder => DeterministicOrdering && !OptimizeThroughput;

    /// <summary>
    ///     The shape to diff an existing table against: its own layout and tie-breaker, except where those were
    ///     set explicitly.
    /// </summary>
    public QueueTableShape ToMatch(Table actual)
    {
        var optimizeThroughput = AdoptLayout ? actual.HasColumn(SeqColumn) : OptimizeThroughput;
        var ordering = DeterministicOrdering &&
                       (!AdoptOrdering || optimizeThroughput || actual.HasColumn(QueueTable.QueueOrderColumn));

        return new QueueTableShape(optimizeThroughput, ordering, false, false);
    }

    public QueueTableShape Fixed => this with { AdoptLayout = false, AdoptOrdering = false };
}
