using JasperFx.Core;
using Shouldly;
using Wolverine.Runtime;
using Wolverine.Runtime.Agents;
using Xunit;

namespace CoreTests.Runtime.Agents;

/// <summary>
/// GH-3698. Every batched agent command carries a <c>Uri[]</c>, and the record default compares those by
/// reference — so two commands naming exactly the same agents were never equal. <c>StartAgents</c> and
/// <c>StopAgents</c> had hand-written <c>SequenceEqual</c> overrides for that reason, but both left
/// <c>GetHashCode</c> returning the array's reference hash, so equal values landed in different buckets and
/// were never compared at all.
///
/// <para>Comparison is now by the actual Uri values and is order-independent: two assignment waves can chunk
/// the same set of agents in a different order, and callers that use equality to recognise the same work
/// twice must not be fooled by that.</para>
/// </summary>
public class batched_agent_command_value_equality
{
    private static readonly NodeDestination Node1 =
        new(Guid.NewGuid(), "fake://one".ToUri());

    private static readonly NodeDestination Node2 =
        new(Guid.NewGuid(), "fake://two".ToUri());

    private static Uri[] Agents(params string[] names)
        => names.Select(x => new Uri($"fake://{x}")).ToArray();

    [Fact]
    public void assign_agents_is_equal_by_agent_values()
    {
        var one = new AssignAgents(Node1, Agents("a", "b", "c"));

        one.ShouldBe(new AssignAgents(Node1, Agents("a", "b", "c")));
        one.GetHashCode().ShouldBe(new AssignAgents(Node1, Agents("a", "b", "c")).GetHashCode());
    }

    [Fact]
    public void assign_agents_ignores_the_order_of_the_agents()
    {
        var one = new AssignAgents(Node1, Agents("a", "b", "c"));
        var shuffled = new AssignAgents(Node1, Agents("c", "a", "b"));

        one.ShouldBe(shuffled);

        // The hash has to agree, or a hash-based lookup never gets far enough to call Equals.
        one.GetHashCode().ShouldBe(shuffled.GetHashCode());
    }

    [Fact]
    public void assign_agents_to_a_different_destination_is_not_equal()
    {
        new AssignAgents(Node1, Agents("a", "b"))
            .ShouldNotBe(new AssignAgents(Node2, Agents("a", "b")));
    }

    [Fact]
    public void assign_agents_with_different_agents_is_not_equal()
    {
        new AssignAgents(Node1, Agents("a", "b"))
            .ShouldNotBe(new AssignAgents(Node1, Agents("a", "c")));

        new AssignAgents(Node1, Agents("a", "b"))
            .ShouldNotBe(new AssignAgents(Node1, Agents("a", "b", "c")));
    }

    [Fact]
    public void a_repeated_agent_must_be_matched_by_the_same_number_of_repeats()
    {
        // Multiset, not set: ["a","a","b"] and ["a","b","b"] have the same length and the same distinct
        // agents, and a set-based comparison would wrongly call them equal.
        new AssignAgents(Node1, Agents("a", "a", "b"))
            .ShouldNotBe(new AssignAgents(Node1, Agents("a", "b", "b")));
    }

    [Fact]
    public void an_empty_batch_is_equal_to_another_empty_batch()
    {
        new AssignAgents(Node1, []).ShouldBe(new AssignAgents(Node1, []));
    }

    [Fact]
    public void stop_remote_agents_is_equal_by_agent_values_regardless_of_order()
    {
        var one = new StopRemoteAgents(Node1, Agents("a", "b", "c"));
        var shuffled = new StopRemoteAgents(Node1, Agents("b", "c", "a"));

        one.ShouldBe(shuffled);
        one.GetHashCode().ShouldBe(shuffled.GetHashCode());
    }

    [Fact]
    public void start_agents_is_equal_by_agent_values_regardless_of_order()
    {
        var one = new StartAgents(Agents("a", "b", "c"));
        var shuffled = new StartAgents(Agents("c", "b", "a"));

        one.ShouldBe(shuffled);
        one.GetHashCode().ShouldBe(shuffled.GetHashCode());
    }

    [Fact]
    public void stop_agents_is_equal_by_agent_values_regardless_of_order()
    {
        var one = new StopAgents(Agents("a", "b", "c"));
        var shuffled = new StopAgents(Agents("c", "b", "a"));

        one.ShouldBe(shuffled);
        one.GetHashCode().ShouldBe(shuffled.GetHashCode());
    }

    [Fact]
    public void agents_started_and_stopped_replies_are_equal_by_agent_values()
    {
        new AgentsStarted(Agents("a", "b")).ShouldBe(new AgentsStarted(Agents("b", "a")));
        new AgentsStopped(Agents("a", "b")).ShouldBe(new AgentsStopped(Agents("b", "a")));
    }

    [Fact]
    public void equal_batches_collapse_in_a_hash_set()
    {
        // The reason all of the above matters: the agent-command pump keys pending work on the command
        // itself, so a re-emitted chunk naming the same agents must not queue a second time.
        var set = new HashSet<IAgentCommand>
        {
            new AssignAgents(Node1, Agents("a", "b", "c")),
            new AssignAgents(Node1, Agents("c", "b", "a")),
            new AssignAgents(Node2, Agents("a", "b", "c"))
        };

        set.Count.ShouldBe(2);
    }
}
