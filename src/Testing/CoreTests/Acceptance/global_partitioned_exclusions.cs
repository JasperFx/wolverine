using Wolverine;
using Wolverine.Runtime.Partitioning;
using Wolverine.Util;
using Xunit;

namespace CoreTests.Acceptance;

public class GlobalPartitionedExclusionTests
{
    public interface IThing;

    public record ThingHappened(string Id) : IThing;

    public record ThingBroadcast(string Id) : IThing;

    public record Unrelated(string Id);

    private static GlobalPartitionedMessageTopology topology()
    {
        return new GlobalPartitionedMessageTopology(new WolverineOptions());
    }

    [Fact]
    public void excluded_type_does_not_match_a_broader_rule()
    {
        var t = topology();
        t.MessagesImplementing<IThing>();
        t.Except<ThingBroadcast>();

        // The whole point: a broad MessagesImplementing rule stays in place so nothing silently
        // drops out of the topology, and only the named type is carved out.
        t.Matches(typeof(ThingHappened)).ShouldBeTrue();
        t.Matches(typeof(ThingBroadcast)).ShouldBeFalse();
    }

    [Fact]
    public void exclusions_win_regardless_of_declaration_order()
    {
        var before = topology();
        before.Except<ThingBroadcast>();
        before.MessagesImplementing<IThing>();
        before.Matches(typeof(ThingBroadcast)).ShouldBeFalse();

        var after = topology();
        after.MessagesImplementing<IThing>();
        after.Except<ThingBroadcast>();
        after.Matches(typeof(ThingBroadcast)).ShouldBeFalse();
    }

    [Fact]
    public void an_exclusion_can_be_an_interface_and_carves_out_the_whole_family()
    {
        var t = topology();
        t.MessagesImplementing<IThing>();
        t.Except<IThing>();

        t.Matches(typeof(ThingHappened)).ShouldBeFalse();
        t.Matches(typeof(ThingBroadcast)).ShouldBeFalse();
    }

    [Fact]
    public void an_explicitly_named_type_can_still_be_excluded()
    {
        var t = topology();
        t.Message<ThingBroadcast>();
        t.Except<ThingBroadcast>();

        t.Matches(typeof(ThingBroadcast)).ShouldBeFalse();

        // MatchesByMessageTypeName is the pre-deserialization path (Kafka), and it reads a separate
        // name cache — an exclusion that only updated Matches() would leak through it.
        t.MatchesByMessageTypeName(typeof(ThingBroadcast).ToMessageTypeName()).ShouldBeFalse();
    }

    [Fact]
    public void excluding_before_naming_also_keeps_the_name_cache_clean()
    {
        var t = topology();
        t.Except<ThingBroadcast>();
        t.Message<ThingBroadcast>();

        t.Matches(typeof(ThingBroadcast)).ShouldBeFalse();
        t.MatchesByMessageTypeName(typeof(ThingBroadcast).ToMessageTypeName()).ShouldBeFalse();
    }

    [Fact]
    public void exclusions_do_not_affect_unrelated_types()
    {
        var t = topology();
        t.MessagesImplementing<IThing>();
        t.Except<ThingBroadcast>();

        t.Matches(typeof(Unrelated)).ShouldBeFalse("never matched in the first place");

        t.Message<Unrelated>();
        t.Matches(typeof(Unrelated)).ShouldBeTrue();
    }

    [Fact]
    public void null_type_is_rejected()
    {
        Should.Throw<ArgumentNullException>(() => topology().Except(null!));
    }
}
