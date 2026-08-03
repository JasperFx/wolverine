using IntegrationTests;
using JasperFx.Events;
using JasperFx.Events.Projections;
using Marten;
using Marten.Events.Aggregation;
using Npgsql;
using Shouldly;
using JasperFx;
using Weasel.Postgresql;
using Weasel.Postgresql.Migrations;
using Wolverine.Runtime.Agents;

namespace MartenTests.MultiTenancy;

/// <summary>
/// End-to-end cover for a projection version bump on a <b>multi-database</b> store, through the real
/// <see cref="EventSubscriptionAgentFamily.EvaluateAssignmentsAsync"/> rather than
/// <see cref="AssignmentGrid.DistributeByGroupAffinity(string, Func{Uri, string}, Func{Uri, bool})"/>
/// directly — so it also covers the store-cardinality pass selection and
/// <c>RetireSupersededAgentsAsync</c>, both of which sit between a leader and the placement.
///
/// <para>The scenario is a blue/green rollout: the "blue" fleet runs the store as it is deployed today
/// and is <i>already running</i> its agents; the "green" fleet runs a build where one projection's
/// <c>Version</c> is one higher. Both fleets are the same application over the same tenant databases,
/// and the leader evaluating the assignments is a blue node — which is what makes the green fleet's
/// agents reachable only through its persisted node capabilities.</para>
/// </summary>
public class blue_green_version_bump_assignment : IAsyncLifetime
{
    private const uint PreviousVersion = 2;
    private const uint BumpedVersion = 3;

    private readonly List<string> _tenants = ["bgtenant1", "bgtenant2", "bgtenant3"];

    private DocumentStore _blue = null!;
    private DocumentStore _green = null!;
    private IReadOnlyList<Uri> _blueAgents = null!;
    private IReadOnlyList<Uri> _greenAgents = null!;

    public async ValueTask InitializeAsync()
    {
        await using var conn = new NpgsqlConnection(Servers.PostgresConnectionString);
        await conn.OpenAsync(TestContext.Current.CancellationToken);

        var connectionStrings = new List<string>();
        foreach (var tenant in _tenants)
        {
            connectionStrings.Add(await CreateDatabaseIfNotExists(conn, tenant));
        }

        await conn.CloseAsync();

        _blue = StoreFor(PreviousVersion, connectionStrings);
        _green = StoreFor(BumpedVersion, connectionStrings);

        _blueAgents = await AdvertisedAgentsAsync(_blue);
        _greenAgents = await AdvertisedAgentsAsync(_green);
    }

    public async ValueTask DisposeAsync()
    {
        await _blue.DisposeAsync();
        await _green.DisposeAsync();
    }

    [Fact]
    public async Task the_two_fleets_advertise_the_bumped_projection_under_disjoint_identities()
    {
        _blueAgents.ShouldNotBeEmpty();
        _greenAgents.ShouldNotBeEmpty();

        var blueBumped = AgentsFor(_blueAgents, "Trip");
        var greenBumped = AgentsFor(_greenAgents, "Trip");

        blueBumped.ShouldNotBeEmpty();
        greenBumped.Count.ShouldBe(blueBumped.Count);
        blueBumped.Intersect(greenBumped).ShouldBeEmpty(
            "a version bump has to change the agent identity, or the leader has no way to tell the two fleets apart");

        // The projection that was NOT bumped is one and the same agent on both fleets, which is what
        // makes it declared by every node and therefore free to land anywhere.
        AgentsFor(_greenAgents, "Passenger")
            .OrderBy(x => x.ToString())
            .ShouldBe(AgentsFor(_blueAgents, "Passenger").OrderBy(x => x.ToString()));
    }

    [Fact]
    public async Task the_bumped_version_is_assigned_to_the_fleet_that_declares_it()
    {
        var (grid, blue, green) = BuildGrid();

        // The leader is a blue node: its own family enumerates only the previous version, exactly as in
        // production, so the green agents reach the grid solely through node capabilities.
        await using var leader = new EventSubscriptionAgentFamily([_blue], []);
        await leader.EvaluateAssignmentsAsync(grid);

        foreach (var uri in AgentsFor(_greenAgents, "Trip"))
        {
            var host = grid.AgentFor(uri).AssignedNode;
            host.ShouldNotBeNull($"{uri} was left unassigned, so nothing would build the new version");
            green.ShouldContain(host!,
                $"{uri} may only run on a node that declares it — BuildAgentAsync throws 'Unknown event projection or subscription' on the other fleet");
        }

        foreach (var uri in AgentsFor(_blueAgents, "Trip"))
        {
            var host = grid.AgentFor(uri).AssignedNode;
            host.ShouldNotBeNull($"{uri} was left unassigned, so the fleet still serving would stop projecting");
            blue.ShouldContain(host!, $"{uri} is the version the blue fleet serves and must stay there");
        }
    }

    // The "one owner per version per database" bound that the sibling-partition preference exists for is
    // asserted in CoreTests.Runtime.Agents.distribute_by_group_affinity instead. It needs agents to
    // outnumber nodes the way they do in a real cluster (~5k agents over ~10 nodes); with three databases
    // over four nodes the per-node ceiling binds first and legitimately spreads a group's third partition
    // to balance load, which is the intended trade and not what this test is about.

    private (AssignmentGrid Grid, List<AssignmentGrid.Node> Blue, List<AssignmentGrid.Node> Green) BuildGrid()
    {
        var grid = new AssignmentGrid();

        var blue = new List<AssignmentGrid.Node>
        {
            grid.WithNode(1, Guid.NewGuid()).HasCapabilities(_blueAgents),
            grid.WithNode(2, Guid.NewGuid()).HasCapabilities(_blueAgents)
        };

        var green = new List<AssignmentGrid.Node>
        {
            grid.WithNode(3, Guid.NewGuid()).HasCapabilities(_greenAgents),
            grid.WithNode(4, Guid.NewGuid()).HasCapabilities(_greenAgents)
        };

        // Blue is already running what it advertises. That incumbency is the whole point: it is what
        // keeps a blue node in the candidate set of a group it can only partly run.
        for (var i = 0; i < blue.Count; i++)
        {
            blue[i].Running(_blueAgents.Where((_, index) => index % blue.Count == i).ToArray());
        }

        // Mirrors NodeAgentController.EvaluateAssignmentsAsync, which seeds the grid from the union of
        // every node's persisted capabilities before handing it to the families.
        grid.WithAgents(_blueAgents.Concat(_greenAgents).Distinct().ToArray());

        return (grid, blue, green);
    }

    private static async Task<IReadOnlyList<Uri>> AdvertisedAgentsAsync(IEventStore store)
    {
        await using var family = new EventSubscriptionAgentFamily([store], []);
        return await family.SupportedAgentsAsync();
    }

    private static DocumentStore StoreFor(uint tripVersion, IReadOnlyList<string> connectionStrings)
    {
        return DocumentStore.For(opts =>
        {
            opts.DatabaseSchemaName = "bluegreen";

            // Nothing here writes or projects; the test only asks the store what it would advertise.
            opts.AutoCreateSchemaObjects = AutoCreate.None;

            opts.MultiTenantedDatabases(tenancy =>
            {
                for (var i = 0; i < connectionStrings.Count; i++)
                {
                    tenancy.AddSingleTenantDatabase(connectionStrings[i], $"bgtenant{i + 1}");
                }
            });

            opts.Projections.Add(new TripProjection { Version = tripVersion }, ProjectionLifecycle.Async);
            opts.Projections.Add(new PassengerProjection(), ProjectionLifecycle.Async);
        });
    }

    private static async Task<string> CreateDatabaseIfNotExists(NpgsqlConnection conn, string databaseName)
    {
        var builder = new NpgsqlConnectionStringBuilder(Servers.PostgresConnectionString);

        if (!await conn.DatabaseExists(databaseName))
        {
            await new DatabaseSpecification().BuildDatabase(conn, databaseName);
        }

        builder.Database = databaseName;
        return builder.ConnectionString;
    }

    private static List<Uri> AgentsFor(IEnumerable<Uri> agents, string projectionName) =>
        agents.Where(uri => uri.Segments.Any(segment =>
            segment.Trim('/').Equals(projectionName, StringComparison.OrdinalIgnoreCase))).ToList();

    // The (type, name, databaseId) prefix of an agent URI, mirroring the internal
    // EventSubscriptionAgentFamily.DatabaseKeyOf that group affinity keys on.
    private static string DatabaseKeyOf(Uri uri) =>
        uri.Segments.Length >= 3
            ? $"{uri.Host}/{uri.Segments[1].Trim('/')}/{uri.Segments[2].Trim('/')}"
            : uri.AbsoluteUri;
}

public record TripStarted(Guid Id, string Description);

public record PassengerBoarded(Guid TripId, string Name);

public class BlueGreenTrip
{
    public Guid Id { get; set; }
    public string Description { get; set; } = string.Empty;
}

public class BlueGreenPassengerCount
{
    public Guid Id { get; set; }
    public int Count { get; set; }
}

public partial class TripProjection : SingleStreamProjection<BlueGreenTrip, Guid>
{
    public TripProjection() => Name = "Trip";

    public static BlueGreenTrip Create(TripStarted started) =>
        new() { Id = started.Id, Description = started.Description };
}

public partial class PassengerProjection : SingleStreamProjection<BlueGreenPassengerCount, Guid>
{
    public PassengerProjection() => Name = "Passenger";

    public static BlueGreenPassengerCount Create(PassengerBoarded boarded) =>
        new() { Id = boarded.TripId, Count = 1 };
}
