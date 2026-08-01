using Shouldly;
using Wolverine.Runtime.Agents;
using Xunit;

namespace CoreTests.Runtime.Agents;

/// <summary>
/// GH-3750. AssignAgents asks a node to start a chunk of agents and gets back the set that actually
/// reached a started state -- StartAgents only bags an agent once StartLocallyAsync returns, so a PARTIAL
/// reply is the normal case whenever starts are slow. The reply used to be discarded entirely: the
/// requested set was logged as "Successfully started" no matter what came back, which made a chunk that
/// half-started indistinguishable from one that fully started.
///
/// This covers the set arithmetic that identifies the unconfirmed remainder.
/// </summary>
public class partially_confirmed_assignments
{
    private static Uri agent(int i) => new($"projection://claim_lines/{i}");

    [Fact]
    public void nothing_missing_when_every_requested_agent_is_confirmed()
    {
        var requested = new[] { agent(1), agent(2), agent(3) };

        AgentUriSet.Missing(requested, [agent(3), agent(1), agent(2)])
            .ShouldBeEmpty();
    }

    [Fact]
    public void identifies_the_unconfirmed_remainder_of_a_partial_reply()
    {
        var requested = new[] { agent(1), agent(2), agent(3), agent(4) };

        AgentUriSet.Missing(requested, [agent(1), agent(3)])
            .ShouldBe([agent(2), agent(4)]);
    }

    [Fact]
    public void everything_is_missing_when_the_node_confirms_nothing()
    {
        var requested = new[] { agent(1), agent(2) };

        AgentUriSet.Missing(requested, []).ShouldBe(requested);
    }

    [Fact]
    public void an_empty_request_is_never_missing_anything()
    {
        AgentUriSet.Missing([], [agent(1)]).ShouldBeEmpty();
    }

    [Fact]
    public void counts_repeats_as_a_multiset_rather_than_collapsing_them()
    {
        // Matches AreEquivalent's multiset semantics: requested twice, confirmed once, still one short.
        AgentUriSet.Missing([agent(1), agent(1)], [agent(1)])
            .ShouldBe([agent(1)]);
    }

    [Fact]
    public void ignores_confirmations_that_were_never_requested()
    {
        AgentUriSet.Missing([agent(1)], [agent(1), agent(9)])
            .ShouldBeEmpty();
    }
}
