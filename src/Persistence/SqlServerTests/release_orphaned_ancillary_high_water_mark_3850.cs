using Shouldly;
using Wolverine.RDBMS.Durability;

namespace SqlServerTests;

/// <summary>
/// GH-3850. The active node numbers are cached per node for up to one polling interval (GH-3846),
/// so the list cannot describe a node that registered after it was taken. Releasing a <i>live</i>
/// node's rows to <c>owner_id = 0</c> hands its in-flight work to somebody else, so the release is
/// bounded by the highest node number the cache has ever seen.
///
/// <para>GH-3971 moved this decision out of the SQL — the sweep now works out the dead owners in memory
/// so it can issue an indexable <c>owner_id in (…)</c> update — so these assert against
/// <see cref="ReleaseOrphanedMessagesCommand.DetermineDeadOwners"/> rather than against statement text.
/// Same rule, now tested where it lives.</para>
/// </summary>
public class release_orphaned_ancillary_high_water_mark_3850
{
    private static int[] dead(int[] ownersInTable, int[] activeNodeNumbers, int highWaterMark)
        => ReleaseOrphanedMessagesCommand.DetermineDeadOwners(ownersInTable, activeNodeNumbers, highWaterMark);

    [Fact]
    public void the_release_is_bounded_by_the_high_water_mark()
    {
        // Node 4 registered after the cached list was taken and has already written rows. Without this
        // bound its live, in-flight work would be reset -- it is absent from the list purely because the
        // list predates it.
        dead([1, 2, 3, 4], activeNodeNumbers: [1, 2, 3], highWaterMark: 3).ShouldBeEmpty();
    }

    [Fact]
    public void a_departed_high_numbered_node_is_still_reclaimable()
    {
        // Node 3 was the highest and has died, so the active list is [1, 2] -- but the mark stays at
        // 3 because the cache saw it. Bounding by max(active) instead would put node 3's orphaned
        // messages permanently out of reach, which is why the mark is monotonic.
        dead([1, 2, 3], activeNodeNumbers: [1, 2], highWaterMark: 3).ShouldBe([3]);
    }

    [Fact]
    public void the_guard_is_omitted_when_no_mark_is_supplied()
    {
        // 0 means "no mark", which restores the un-bounded behaviour rather than releasing nothing.
        dead([1, 2, 3, 9], activeNodeNumbers: [1, 2, 3], highWaterMark: 0).ShouldBe([9]);
    }

    [Fact]
    public void unowned_rows_are_never_released()
    {
        // owner_id = 0 IS the released state. Including it would rewrite every unowned row on every
        // sweep -- the largest share of a busy inbox -- for no effect at all.
        dead([0, 1, 5], activeNodeNumbers: [1], highWaterMark: 0).ShouldBe([5]);
    }

    [Fact]
    public void a_fleet_with_nothing_orphaned_yields_nothing()
    {
        // The steady state, and the point of GH-3971: every owner present is live, so there is nothing to
        // release and the sweep issues no UPDATE at all.
        dead([1, 2, 3], activeNodeNumbers: [1, 2, 3], highWaterMark: 3).ShouldBeEmpty();
    }
}
