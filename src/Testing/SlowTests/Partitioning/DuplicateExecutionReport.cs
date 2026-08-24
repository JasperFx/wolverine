using JasperFx.Core;
using Wolverine.ComplianceTests.Partitioning;

namespace SlowTests.Partitioning;

/// <summary>
/// GH-3713. The measurement this reproduction exists to produce: how many times a webhook event that the
/// cluster had <i>already executed</i> got executed again, in steady state, across a hard node kill, and
/// across a rolling deploy.
/// </summary>
/// <remarks>
/// <para><b>Duplicate execution, not duplicate delivery.</b> These are not the same quantity and the
/// difference is the whole point of the measurement. An unacked delivery that a dying node never got around
/// to running is redelivered and then runs for the <i>first</i> time -- a duplicate delivery in the broker's
/// accounting, but not a duplicate execution, and nothing a user's handler can tell apart from a normal
/// first delivery. A duplicate <i>execution</i> only happens when the handler completed and the ack did not
/// reach the broker, which is a strictly smaller set. Handler-visible at-least-once behaviour is the second
/// quantity, so that is the one reported here.</para>
///
/// <para>Both are compared against the prefetch window, which is what
/// <c>docs/guide/messaging/listeners.md</c> names as the bound.</para>
/// </remarks>
internal sealed record DuplicateExecutionReport(
    string Phase,
    int Published,
    int DistinctExecuted,
    int TotalExecutions,
    int DuplicateExecutions,
    int PrefetchPerSlot,
    int SlotCount,
    int DisruptedNodes,
    int PeakBacklog,
    int UnackedAtDisruption)
{
    /// <summary>
    /// Duplicate executions as a percentage of the events that were executed at all.
    /// </summary>
    public double DuplicateRatePercent =>
        DistinctExecuted == 0 ? 0 : 100.0 * DuplicateExecutions / DistinctExecuted;

    /// <summary>
    /// The bound the documentation currently claims, read as generously as it can honestly be read: every
    /// slot that changed hands could have had its whole prefetch window unacked at that instant.
    /// </summary>
    public int DocumentedPrefetchBound => PrefetchPerSlot * Math.Max(1, DisruptedNodes) * SlotCount;

    /// <summary>
    /// Read the ledger and compute the report. <paramref name="disruptedNodes" /> is how many nodes went away
    /// during the phase -- zero for a steady-state run, which the bound treats as one so that the comparison
    /// is still meaningful rather than zero.
    /// </summary>
    public static DuplicateExecutionReport From(string phase,
        IReadOnlyCollection<(string GroupId, int Sequence)> published, int prefetchPerSlot, int slotCount,
        int disruptedNodes, int peakBacklog, int unackedAtDisruption)
    {
        var handled = NativeAckPartitionedProcessing.Ledger.Handled;
        var distinct = handled.Select(x => (x.GroupId, x.Sequence)).Distinct().Count();

        return new DuplicateExecutionReport(phase, published.Count, distinct, handled.Count,
            handled.Count - distinct, prefetchPerSlot, slotCount, disruptedNodes, peakBacklog,
            unackedAtDisruption);
    }

    /// <summary>
    /// A single grep-able block, because the number is the deliverable and it has to survive being read out
    /// of a CI log rather than out of a debugger.
    /// </summary>
    public string Describe()
    {
        var lines = new List<string>
        {
            "",
            "=== GH-3713 DUPLICATE EXECUTION REPORT ===============================",
            $"  phase                      : {Phase}",
            $"  nodes disrupted            : {DisruptedNodes}",
            $"  slots x prefetch per slot  : {SlotCount} x {PrefetchPerSlot}",
            $"  published (accepted)       : {Published}",
            $"  peak broker backlog        : {PeakBacklog}   (proves the flood saturated)",
            $"  broker unacked at chaos    : {UnackedAtDisruption}   (the prefetch window the docs bound by)",
            $"  distinct events executed   : {DistinctExecuted}",
            $"  total handler executions   : {TotalExecutions}",
            $"  DUPLICATE EXECUTIONS       : {DuplicateExecutions}",
            $"  DUPLICATE RATE             : {DuplicateRatePercent:F3}%",
            $"  documented prefetch bound  : {DocumentedPrefetchBound}",
            $"  within documented bound    : {(DuplicateExecutions <= DocumentedPrefetchBound ? "yes" : "NO")}",
            "====================================================================",
            ""
        };

        return lines.Join(Environment.NewLine);
    }
}
