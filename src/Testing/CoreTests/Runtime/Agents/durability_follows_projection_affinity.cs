using JasperFx.Descriptors;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Wolverine.ComplianceTests;
using Wolverine.Persistence;
using Wolverine.Runtime;
using Wolverine.Runtime.Agents;
using Xunit;

namespace CoreTests.Runtime.Agents;

// GH-3785: database affinity is a property of the DATABASE, not of each agent family independently. The
// event-subscription family already keeps a shard database's projection agents together on one node
// (GH-3792 / marten#4806); the durability agent for that same database must land on that node too, or the
// database attracts two nodes' connection pools instead of one — measured on a 512-shard production
// cluster as 73% of databases split, ~425 connections held by durability owners against databases they
// otherwise never touch.
public class durability_follows_projection_affinity
{
    private static string DatabaseKey(Uri uri) => uri.Segments[2].Trim('/');

    // event-subscriptions://{type}/{name}/{databaseId}/{shard...} — the databaseId slot carries
    // DatabaseId.ToString(), which is how EventSubscriptionAgentFamily.DatabaseIdOf parses it back.
    private static Uri Projection(string server, string db, string tenant) =>
        new($"event-subscriptions://marten/main/{new DatabaseId(server, db)}/Proj/All/v7/{tenant}");

    // wolverinedb://{engine}/{server}/{database}/{schema} — the grammar MessageDatabase builds.
    private static Uri Durability(string server, string db) =>
        new($"wolverinedb://postgresql/{server}/{db}/wolverine");

    private static void distribute(AssignmentGrid grid)
    {
        // The two passes in the order NodeAgentController now guarantees: event subscriptions first,
        // durability after, over the same grid.
        grid.DistributeByGroupAffinity("event-subscriptions", DatabaseKey);
        grid.DistributeEvenlyWithAffinity("wolverinedb", DurabilityProjectionAffinityAccess.BuildPreference(grid));
    }

    [Fact]
    public void a_databases_durability_agent_lands_with_its_projection_owner()
    {
        var databases = new[] { "tenant1", "tenant2", "tenant3", "tenant4" };
        var tenants = new[] { "t1", "t2", "t3" };

        var projections = databases
            .SelectMany(db => tenants.Select(t => Projection("localhost", db, t)))
            .ToArray();
        var durability = databases.Select(db => Durability("localhost", db)).ToArray();

        var grid = new AssignmentGrid();
        grid.WithNode(1, Guid.NewGuid());
        grid.WithNode(2, Guid.NewGuid());
        grid.WithAgents(projections.Concat(durability).ToArray());

        distribute(grid);

        foreach (var db in databases)
        {
            var projectionOwner = grid.AgentFor(Projection("localhost", db, "t1")).AssignedNode;
            projectionOwner.ShouldNotBeNull();

            grid.AgentFor(Durability("localhost", db)).AssignedNode.ShouldBe(projectionOwner,
                $"{db}'s durability agent must live with {db}'s projections so the database attracts one pool, not two");
        }
    }

    [Fact]
    public void a_durability_agent_running_elsewhere_is_moved_to_the_projection_owner()
    {
        // The one-time migration that converges an existing cluster: the durability agent is RUNNING on
        // node 2, the projections land on node 1, and the agent must move — surfacing as a normal
        // ReassignAgent command downstream.
        var projections = new[] { "t1", "t2" }.Select(t => Projection("localhost", "tenant1", t)).ToArray();
        var durabilityUri = Durability("localhost", "tenant1");

        var grid = new AssignmentGrid();
        var node1 = grid.WithNode(1, Guid.NewGuid());
        node1.Running(projections);
        var node2 = grid.WithNode(2, Guid.NewGuid());
        node2.Running(durabilityUri);

        distribute(grid);

        grid.AgentFor(durabilityUri).AssignedNode.ShouldBe(node1,
            "the durability agent follows the projections even when it means moving a running agent once");
    }

    [Fact]
    public void the_settled_co_located_state_is_a_fixed_point()
    {
        // Once durability and projections share a node, re-evaluating must move nothing — churn here is
        // agent restarts at 512-database scale.
        var databases = new[] { "tenant1", "tenant2", "tenant3", "tenant4" };
        var tenants = new[] { "t1", "t2" };

        var first = new AssignmentGrid();
        first.WithNode(1, Guid.NewGuid());
        first.WithNode(2, Guid.NewGuid());
        first.WithAgents(databases
            .SelectMany(db => tenants.Select(t => Projection("localhost", db, t)).Append(Durability("localhost", db)))
            .ToArray());

        distribute(first);
        var placement = first.AllAgents.ToDictionary(a => a.Uri, a => a.AssignedNode!.NodeId);

        var ids = first.Nodes.ToDictionary(n => n.NodeId, n => n.AssignedId);
        var second = new AssignmentGrid();
        var nodesById = ids.ToDictionary(pair => pair.Key, pair => second.WithNode(pair.Value, pair.Key));
        foreach (var byNode in placement.GroupBy(kv => kv.Value))
        {
            nodesById[byNode.Key].Running(byNode.Select(kv => kv.Key).ToArray());
        }

        distribute(second);

        second.AllAgents
            .Where(a => a.AssignedNode!.NodeId != placement[a.Uri])
            .ShouldBeEmpty("the co-located state must be a fixed point of both distributions");
    }

    [Fact]
    public void a_database_with_no_projections_still_gets_an_even_spread()
    {
        // The main message store (or any database with no async projections) has nothing to follow; those
        // agents keep today's even distribution and are never stranded.
        var durability = Enumerable.Range(1, 6).Select(i => Durability("localhost", $"plain{i}")).ToArray();

        var grid = new AssignmentGrid();
        grid.WithNode(1, Guid.NewGuid());
        grid.WithNode(2, Guid.NewGuid());
        grid.WithAgents(durability);

        distribute(grid);

        grid.AllAgents.ShouldAllBe(a => a.AssignedNode != null);
        grid.Nodes.Select(n => n.ForScheme("wolverinedb").Count())
            .ShouldAllBe(count => count == 3, "with no affinity available the spread stays even");
    }

    // GH-3785 diagnostics. The join falls back silently whenever it cannot match, which is the correct
    // runtime behavior and an unusable diagnostic story: a join that never fires because the two families
    // spell the same database differently looks exactly like the feature working. These pin the counters
    // that tell an operator which one they got.

    [Fact]
    public void the_affinity_report_counts_what_actually_joined()
    {
        var databases = new[] { "tenant1", "tenant2", "tenant3" };
        var projections = databases.Select(db => Projection("localhost", db, "t1")).ToArray();
        var durability = databases.Select(db => Durability("localhost", db)).ToArray();

        var grid = new AssignmentGrid();
        grid.WithNode(1, Guid.NewGuid());
        grid.WithNode(2, Guid.NewGuid());
        grid.WithAgents(projections.Concat(durability).ToArray());

        grid.DistributeByGroupAffinity("event-subscriptions", DatabaseKey);
        var preference = DurabilityProjectionAffinityAccess.BuildPreferenceReport(grid);
        grid.DistributeEvenlyWithAffinity("wolverinedb", preference.NodeFor);

        preference.KnownDatabases.ShouldBe(3);
        preference.Considered.ShouldBe(3);
        preference.Matched.ShouldBe(3);
    }

    [Fact]
    public void a_spelling_divergence_between_the_two_pipelines_reports_zero_matches()
    {
        // THE failure this diagnostic exists for. Both families describe the same three physical databases,
        // but through descriptor pipelines that disagree about the name — so nothing joins, every durability
        // agent silently falls back to the even spread, and the cluster keeps paying the two-pools-per-shard
        // cost with no outward sign. Known and Considered are both non-zero while Matched is zero, which is
        // the shape that has to be distinguishable from "this app simply has no projections".
        var projections = new[] { "tenant1", "tenant2", "tenant3" }
            .Select(db => Projection("localhost", db, "t1")).ToArray();
        var durability = new[] { "tenant_1", "tenant_2", "tenant_3" }
            .Select(db => Durability("localhost", db)).ToArray();

        var grid = new AssignmentGrid();
        grid.WithNode(1, Guid.NewGuid());
        grid.WithNode(2, Guid.NewGuid());
        grid.WithAgents(projections.Concat(durability).ToArray());

        grid.DistributeByGroupAffinity("event-subscriptions", DatabaseKey);
        var preference = DurabilityProjectionAffinityAccess.BuildPreferenceReport(grid);
        grid.DistributeEvenlyWithAffinity("wolverinedb", preference.NodeFor);

        preference.KnownDatabases.ShouldBe(3);
        preference.Considered.ShouldBe(3);
        preference.Matched.ShouldBe(0);

        // ...and the fallback still placed everything. A miss is never wrong, only not-better.
        grid.AllAgents.ShouldAllBe(a => a.AssignedNode != null);
    }

    [Fact]
    public void a_durability_uri_that_names_no_database_is_not_counted_as_a_miss()
    {
        // The null store and the multi-tenanted composite marker are not databases. Counting them as
        // considered-but-unmatched would show a permanent shortfall on a healthy cluster and train an
        // operator to ignore the number.
        var projections = new[] { Projection("localhost", "tenant1", "t1") };
        var durability = new[] { Durability("localhost", "tenant1"), new Uri("wolverinedb://nullstore") };

        var grid = new AssignmentGrid();
        grid.WithNode(1, Guid.NewGuid());
        grid.WithNode(2, Guid.NewGuid());
        grid.WithAgents(projections.Concat(durability).ToArray());

        grid.DistributeByGroupAffinity("event-subscriptions", DatabaseKey);
        var preference = DurabilityProjectionAffinityAccess.BuildPreferenceReport(grid);
        grid.DistributeEvenlyWithAffinity("wolverinedb", preference.NodeFor);

        preference.Considered.ShouldBe(1, "only the URI that actually names a database is eligible");
        preference.Matched.ShouldBe(1);
        grid.AllAgents.ShouldAllBe(a => a.AssignedNode != null);
    }

    [Fact]
    public void an_application_with_no_projections_reports_nothing_to_follow()
    {
        // Distinguishable from the divergence case above by KnownDatabases == 0: there was never anything
        // to co-locate with, so there is nothing to warn about.
        var durability = Enumerable.Range(1, 4).Select(i => Durability("localhost", $"plain{i}")).ToArray();

        var grid = new AssignmentGrid();
        grid.WithNode(1, Guid.NewGuid());
        grid.WithNode(2, Guid.NewGuid());
        grid.WithAgents(durability);

        var preference = DurabilityProjectionAffinityAccess.BuildPreferenceReport(grid);
        grid.DistributeEvenlyWithAffinity("wolverinedb", preference.NodeFor);

        preference.KnownDatabases.ShouldBe(0);
        preference.Matched.ShouldBe(0);
    }

    [Fact]
    public void the_report_is_written_once_and_then_stays_quiet_until_it_changes()
    {
        // Assignment is re-evaluated on every health check. A settled cluster reports the same numbers
        // forever, and an unconditional log line at that cadence is noise an operator learns to filter —
        // which defeats the point of having it at all.
        var databases = new[] { "tenant1", "tenant2" };
        var projections = databases.Select(db => Projection("localhost", db, "t1")).ToArray();
        var durability = databases.Select(db => Durability("localhost", db)).ToArray();

        var logger = new RecordingLogger();
        (int Known, int Considered, int Matched) last = default;

        for (var i = 0; i < 5; i++)
        {
            var grid = new AssignmentGrid();
            grid.WithNode(1, Guid.NewGuid());
            grid.WithNode(2, Guid.NewGuid());
            grid.WithAgents(projections.Concat(durability).ToArray());

            grid.DistributeByGroupAffinity("event-subscriptions", DatabaseKey);
            var preference = DurabilityProjectionAffinityAccess.BuildPreferenceReport(grid);
            grid.DistributeEvenlyWithAffinity("wolverinedb", preference.NodeFor);

            preference.ReportTo(logger, ref last);
        }

        logger.Messages.Count.ShouldBe(1, "five identical evaluations are one event, not five");
        logger.Messages.Single().Level.ShouldBe(LogLevel.Information);
    }

    [Fact]
    public void the_fail_silent_case_is_logged_as_a_warning()
    {
        var projections = new[] { Projection("localhost", "tenant1", "t1") };
        var durability = new[] { Durability("localhost", "tenant_1") };

        var grid = new AssignmentGrid();
        grid.WithNode(1, Guid.NewGuid());
        grid.WithNode(2, Guid.NewGuid());
        grid.WithAgents(projections.Concat(durability).ToArray());

        grid.DistributeByGroupAffinity("event-subscriptions", DatabaseKey);
        var preference = DurabilityProjectionAffinityAccess.BuildPreferenceReport(grid);
        grid.DistributeEvenlyWithAffinity("wolverinedb", preference.NodeFor);

        var logger = new RecordingLogger();
        (int Known, int Considered, int Matched) last = default;
        preference.ReportTo(logger, ref last);

        var message = logger.Messages.ShouldHaveSingleItem();
        message.Level.ShouldBe(LogLevel.Warning);
        message.Text.ShouldContain("GH-3785");
    }

    [Fact]
    public void an_application_with_no_projections_logs_nothing_at_all()
    {
        var grid = new AssignmentGrid();
        grid.WithNode(1, Guid.NewGuid());
        grid.WithNode(2, Guid.NewGuid());
        grid.WithAgents(Durability("localhost", "plain1"), Durability("localhost", "plain2"));

        var preference = DurabilityProjectionAffinityAccess.BuildPreferenceReport(grid);
        grid.DistributeEvenlyWithAffinity("wolverinedb", preference.NodeFor);

        var logger = new RecordingLogger();
        (int Known, int Considered, int Matched) last = default;
        preference.ReportTo(logger, ref last);

        logger.Messages.ShouldBeEmpty("nothing to co-locate with is not a finding");
    }

    [Fact]
    public void the_same_database_name_on_two_servers_is_disambiguated_by_server()
    {
        // Two servers each carry a database literally named "app": the join must not guess. The durability
        // agent for server-a's "app" follows server-a's projections, and vice versa.
        var grid = new AssignmentGrid();
        var node1 = grid.WithNode(1, Guid.NewGuid());
        var node2 = grid.WithNode(2, Guid.NewGuid());

        node1.Running(Projection("server-a", "app", "t1"), Projection("server-a", "app", "t2"));
        node2.Running(Projection("server-b", "app", "t1"), Projection("server-b", "app", "t2"));

        grid.WithAgents(Durability("server-a", "app"), Durability("server-b", "app"));

        distribute(grid);

        grid.AgentFor(Durability("server-a", "app")).AssignedNode.ShouldBe(node1);
        grid.AgentFor(Durability("server-b", "app")).AssignedNode.ShouldBe(node2);
    }

    [Fact]
    public void an_ambiguous_name_with_an_unrecognized_server_spelling_falls_back_to_even()
    {
        // Ambiguous name AND a server spelling matching neither candidate: affinity must decline rather
        // than guess — a miss is never wrong, only not-better — and the agent still gets assigned.
        var grid = new AssignmentGrid();
        var node1 = grid.WithNode(1, Guid.NewGuid());
        var node2 = grid.WithNode(2, Guid.NewGuid());

        node1.Running(Projection("server-a", "app", "t1"));
        node2.Running(Projection("server-b", "app", "t1"));

        var orphan = Durability("server-c", "app");
        grid.WithAgents(orphan);

        distribute(grid);

        grid.AgentFor(orphan).AssignedNode.ShouldNotBeNull("no affinity is no reason to strand the agent");
    }

    [Fact]
    public void differing_port_spellings_still_join()
    {
        // The two families describe the same physical server through different pipelines, and either side
        // may carry a port the other doesn't. The join has to survive that or it silently never engages —
        // which would look exactly like the feature working, minus the benefit.
        var grid = new AssignmentGrid();
        var node1 = grid.WithNode(1, Guid.NewGuid());
        var node2 = grid.WithNode(2, Guid.NewGuid());

        node1.Running(Projection("db-host:5433", "app", "t1"));
        node2.Running(Projection("other-host", "app", "t1"));

        var durability = Durability("db-host", "app");
        grid.WithAgents(durability);

        distribute(grid);

        grid.AgentFor(durability).AssignedNode.ShouldBe(node1,
            "db-host:5433 and db-host are the same server; the port must not defeat the join");
    }

    [Fact]
    public void during_a_blue_green_split_the_durability_agent_follows_the_larger_side()
    {
        // Mid-rollout a database legitimately has one owner per projection version. The durability agent
        // follows whichever node holds more of the database's agents — deterministically, so it does not
        // flap between the two owners across evaluations.
        var grid = new AssignmentGrid();
        var blue = grid.WithNode(1, Guid.NewGuid());
        var green = grid.WithNode(2, Guid.NewGuid());

        blue.Running(Projection("localhost", "tenant1", "t1"), Projection("localhost", "tenant1", "t2"));
        green.Running(Projection("localhost", "tenant1", "t3"));

        var durability = Durability("localhost", "tenant1");
        grid.WithAgents(durability);

        distribute(grid);

        grid.AgentFor(durability).AssignedNode.ShouldBe(blue,
            "two of tenant1's three agents are on blue, so blue is the cheaper co-location");
    }

    [Fact]
    public async Task the_durability_family_always_evaluates_after_the_others()
    {
        // The affinity only works because the durability family sees the event-subscription assignments
        // in the shared grid — which was previously true only by Dictionary insertion-order accident.
        // Register the durability-schemed family FIRST and assert it still evaluates LAST.
        var calls = new List<string>();

        IAgentFamily recording(string scheme)
        {
            var family = Substitute.For<IAgentFamily>();
            family.Scheme.Returns(scheme);
            family.AllKnownAgentsAsync().Returns(new ValueTask<IReadOnlyList<Uri>>(Array.Empty<Uri>()));
            family.EvaluateAssignmentsAsync(Arg.Any<AssignmentGrid>())
                .Returns(_ =>
                {
                    calls.Add(scheme);
                    return ValueTask.CompletedTask;
                });
            return family;
        }

        var options = new WolverineOptions { ApplicationAssembly = GetType().Assembly };
        options.Durability.DurabilityAgentEnabled = false;

        var runtime = Substitute.For<IWolverineRuntime>();
        runtime.Options.Returns(options);
        runtime.DurabilitySettings.Returns(options.Durability);
        runtime.Observer.Returns(Substitute.For<IWolverineObserver>());

        var controller = new NodeAgentController(runtime, Substitute.For<INodeAgentPersistence>(),
            [recording("wolverinedb"), recording("event-subscriptions"), recording("fake")],
            NullLogger<NodeAgentController>.Instance, CancellationToken.None);

        var node = new WolverineNode
        {
            NodeId = options.UniqueNodeId,
            AssignedNodeNumber = 1,
            ControlUri = new Uri("fake://node0")
        };

        await controller.EvaluateAssignmentsAsync([node], new AgentRestrictions());

        calls.Last().ShouldBe("wolverinedb",
            "the durability family must evaluate after every other family so cross-family affinity can see their assignments");
        calls.Count.ShouldBe(3);
    }

    private sealed class RecordingLogger : ILogger
    {
        public List<(LogLevel Level, string Text)> Messages { get; } = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            Messages.Add((logLevel, formatter(state, exception)));
        }
    }
}

// DurabilityProjectionAffinity is internal to Wolverine; CoreTests has InternalsVisibleTo, and this alias
// keeps the intent readable at the call sites above.
internal static class DurabilityProjectionAffinityAccess
{
    internal static Func<Uri, AssignmentGrid.Node?> BuildPreference(AssignmentGrid grid)
        => Wolverine.Persistence.DurabilityProjectionAffinity.BuildPreference(grid).NodeFor;

    internal static DurabilityAffinityPreference BuildPreferenceReport(AssignmentGrid grid)
        => Wolverine.Persistence.DurabilityProjectionAffinity.BuildPreference(grid);
}
