using IntegrationTests;
using JasperFx;
using JasperFx.Events;
using JasperFx.Events.Projections;
using Polecat;
using PolecatTests.Distribution.TripDomain;
using Shouldly;
using Wolverine.Runtime.Agents;
using Xunit;

namespace PolecatTests.Distribution;

/// <summary>
/// The Polecat half of the GH-4562 cover that <c>MartenTests.MultiTenancy.blue_green_version_bump_assignment</c>
/// provides for Marten: a blue/green rollout placed through the real
/// <see cref="EventSubscriptionAgentFamily"/> and <see cref="AssignmentGrid.DistributeByGroupAffinity(string, Func{Uri, string}, Func{Uri, bool})"/>,
/// over agent URIs a real Polecat store advertises rather than hand-written ones.
///
/// <para>It is a structural twin, not a copy. The Marten version gets a group deep enough to split from
/// several tenants sharing one shard database, which Polecat cannot express --
/// <c>Polecat.Storage.SeparateDatabaseTenancy</c> offers only <c>AddTenant</c>, so Polecat multi-tenancy
/// is one tenant per database. Here the depth comes from several PROJECTIONS on one database instead.
/// The mechanism under test is unchanged: a partition holding more than one agent, of which a blue node
/// runs some without declaring them.</para>
///
/// <para>Deliberately single-database. A multi-tenanted Polecat store reports
/// <c>Cardinality = Single</c> with an empty <c>Databases</c> collection from <c>TryCreateUsage()</c>,
/// even though <c>IEventStore.AllDatabases()</c> on the same store returns every tenant database
/// (JasperFx/polecat#675). <c>EventStoreAgents.SupportedAgentsAsync</c> reads the usage descriptor, so
/// every agent would collapse onto the main database and there would be nothing left to group. Using one
/// database keeps this test about placement rather than about that gap; when #675 is fixed this is worth
/// revisiting as the full multi-database twin of the Marten version.</para>
/// </summary>
public class blue_green_version_bump_assignment
{
    // The projection both fleets serve unchanged -- declared by every node, so it is its own partition
    // and free to stay where it is running.
    private const string Unchanged = "day";

    // The projection being version-bumped: blue serves version 1, green version 2. Each version is a
    // separate shard identity, and only the fleet running that build can BUILD its agent.
    private const string Bumped = "trip";

    // Registered on green only. This is the Polecat stand-in for the tenant provisioned after the blue
    // nodes captured their capability snapshots: blue is happily RUNNING it and does not declare it.
    private const string AfterBlueSnapshot = "distance";

    [Fact]
    public async Task the_bumped_version_is_not_dragged_onto_a_blue_node_by_a_grandfathered_agent()
    {
        var blueAgents = await AdvertisedAgentsAsync(BlueStore());
        var greenAgents = await AdvertisedAgentsAsync(GreenStore());

        // Guard the shape this test exists for. Without it, a change that stopped versioning shard names
        // would silently make both fleets advertise one and the same set, every agent would be declared
        // everywhere, and the whole file would go on passing while testing nothing.
        var bumpedOnGreen = Only(greenAgents, Bumped);
        bumpedOnGreen.ShouldNotBeEmpty("green must advertise the bumped projection");
        bumpedOnGreen.ShouldAllBe(uri => !blueAgents.Contains(uri),
            "the two fleets must advertise the bumped projection under DISJOINT identities, or there is no blue/green gap to test");

        var grandfathered = Only(greenAgents, AfterBlueSnapshot).Single();
        blueAgents.ShouldNotContain(grandfathered,
            "the stand-in for a post-snapshot tenant must be absent from blue's capabilities");

        var grid = new AssignmentGrid();
        var blue = grid.WithNode(1, Guid.NewGuid()).HasCapabilities(blueAgents);
        var green = grid.WithNode(2, Guid.NewGuid()).HasCapabilities(greenAgents);

        // Blue runs everything it declares, PLUS the agent it does not declare. That last one is what
        // used to pull blue into the candidate set for a partition it can only partly run.
        blue.Running(blueAgents.Append(grandfathered).ToArray());

        grid.WithAgents(blueAgents.Concat(greenAgents).Distinct().ToArray());

        grid.DistributeByGroupAffinity("event-subscriptions", EventSubscriptionAgentFamily.DatabaseKeyOf);

        foreach (var uri in bumpedOnGreen)
        {
            var host = grid.AgentFor(uri).AssignedNode;
            host.ShouldNotBeNull($"{uri} was left unassigned, so nothing would build the new version");
            host.ShouldBe(green,
                $"{uri} may only run on the fleet that declares it -- BuildAgentAsync throws 'Unable to find a shard' on the other one");
        }

        foreach (var uri in Only(blueAgents, Bumped))
        {
            grid.AgentFor(uri).AssignedNode.ShouldBe(blue,
                $"{uri} is the version the blue fleet serves and must stay there");
        }
    }

    private static List<Uri> Only(IEnumerable<Uri> agents, string projectionName) =>
        agents.Where(uri => uri.Segments.Any(segment =>
            segment.Trim('/').StartsWith(projectionName, StringComparison.OrdinalIgnoreCase))).ToList();

    private static async Task<IReadOnlyList<Uri>> AdvertisedAgentsAsync(IDocumentStore store)
    {
        await using var family = new EventSubscriptionAgentFamily([(IEventStore)store], []);
        return await family.SupportedAgentsAsync();
    }

    // The build deployed today.
    private static IDocumentStore BlueStore() => StoreFor(1, withPostSnapshotProjection: false);

    // The build rolling out: the bumped projection, and one registered since blue started.
    private static IDocumentStore GreenStore() => StoreFor(2, withPostSnapshotProjection: true);

    private static IDocumentStore StoreFor(uint tripVersion, bool withPostSnapshotProjection)
    {
        return DocumentStore.For(opts =>
        {
            opts.DatabaseSchemaName = "polecat_bluegreen";
            opts.ConnectionString = Servers.SqlServerConnectionString;

            // Nothing here writes or projects; the test only asks each store what it would advertise.
            opts.AutoCreateSchemaObjects = AutoCreate.None;

            opts.Projections.Add(new TripProjection { Version = tripVersion }, ProjectionLifecycle.Async);
            opts.Projections.Add<DayProjection>(ProjectionLifecycle.Async);

            if (withPostSnapshotProjection)
            {
                opts.Projections.Add<DistanceProjection>(ProjectionLifecycle.Async);
            }
        });
    }
}
