using Microsoft.Data.SqlClient;
using NSubstitute;
using Shouldly;
using Weasel.Core;
using Wolverine.RDBMS;
using Wolverine.RDBMS.Durability;

namespace SqlServerTests;

/// <summary>
/// GH-3850. The active node numbers are cached per node for up to one polling interval (GH-3846),
/// so the list cannot describe a node that registered after it was taken. Releasing a <i>live</i>
/// node's rows to <c>owner_id = 0</c> hands its in-flight work to somebody else, so the release is
/// bounded by the highest node number the cache has ever seen.
/// </summary>
public class release_orphaned_ancillary_high_water_mark_3850
{
    private static string sqlFor(IReadOnlyList<int> activeNodeNumbers, int highWaterMark)
    {
        var database = Substitute.For<IMessageDatabase>();
        database.SchemaName.Returns("ancillary");

        var operation = new ReleaseOrphanedMessagesForAncillaryOperation(database, activeNodeNumbers,
            highWaterMark);

        var builder = new DbCommandBuilder(new SqlCommand());
        operation.ConfigureCommand(builder);

        return builder.Compile().CommandText;
    }

    [Fact]
    public void the_release_is_bounded_by_the_high_water_mark()
    {
        var sql = sqlFor([1, 2, 3], highWaterMark: 3);

        // Node 4 registered after the cached list was taken. Without this bound the update would
        // reset its rows -- it is absent from the list purely because the list predates it.
        sql.ShouldContain("owner_id <= 3");
    }

    [Fact]
    public void a_departed_high_numbered_node_is_still_reclaimable()
    {
        // Node 3 was the highest and has died, so the active list is [1, 2] -- but the mark stays at
        // 3 because the cache saw it. Bounding by max(active) instead would put node 3's orphaned
        // messages permanently out of reach, which is why the mark is monotonic.
        var sql = sqlFor([1, 2], highWaterMark: 3);

        sql.ShouldContain("owner_id <= 3");
        sql.ShouldContain("owner_id not in (1, 2)");
    }

    [Fact]
    public void the_guard_is_omitted_when_no_mark_is_supplied()
    {
        // 0 means "no mark", which restores the un-bounded behaviour rather than releasing nothing.
        var sql = sqlFor([1, 2, 3], highWaterMark: 0);

        sql.ShouldNotContain("owner_id <=");
        sql.ShouldContain("owner_id not in (1, 2, 3)");
    }

    [Fact]
    public void both_the_inbox_and_the_outbox_are_bounded()
    {
        var sql = sqlFor([1, 2], highWaterMark: 7);

        sql.ShouldContain("ancillary.wolverine_incoming");
        sql.ShouldContain("ancillary.wolverine_outgoing");

        // one bound per statement -- an unbounded outbox release is the same defect, and the outbox
        // is where a newcomer owns rows first (MessageRoute stamps OwnerId on persist)
        System.Text.RegularExpressions.Regex.Matches(sql, "owner_id <= 7").Count.ShouldBe(2);
    }
}
