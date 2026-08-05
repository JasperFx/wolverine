using IntegrationTests;
using JasperFx;
using JasperFx.Events.Projections;
using Marten;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Shouldly;
using Weasel.Postgresql;
using Weasel.Postgresql.Migrations;
using Wolverine;
using Wolverine.Persistence;
using Wolverine.Persistence.Durability;
using Wolverine.Postgresql;
using Wolverine.RDBMS;
using Wolverine.Runtime.Agents;

namespace MartenTests.MultiTenancy;

/// <summary>
/// GH-3785, the half a unit test cannot cover: the cross-family affinity joins a
/// <c>wolverinedb://</c> agent URI (built by <c>MessageDatabase</c> from a Weasel descriptor) to an
/// <c>event-subscriptions://</c> agent URI (built from a Marten database descriptor), and the two
/// families describe the same physical database through entirely different pipelines. If their server
/// or database spellings ever diverge, the join silently never engages — which looks exactly like the
/// feature working, minus the benefit. So this runs both REAL pipelines against the same three tenant
/// databases and asserts the join actually connects them.
/// </summary>
public class durability_projection_affinity_real_stores : IAsyncLifetime
{
    private static readonly string[] _databases = ["affshard1", "affshard2", "affshard3"];
    private static readonly string[] _tenantsPerDatabase = ["alpha", "beta", "gamma"];

    private DocumentStore _martenStore = null!;
    private readonly List<PostgresqlMessageStore> _messageStores = [];
    private IReadOnlyList<Uri> _projectionAgents = null!;

    public async ValueTask InitializeAsync()
    {
        await using var conn = new NpgsqlConnection(Servers.PostgresConnectionString);
        await conn.OpenAsync(TestContext.Current.CancellationToken);

        var connectionStrings = new List<string>();
        foreach (var database in _databases)
        {
            var builder = new NpgsqlConnectionStringBuilder(Servers.PostgresConnectionString);
            if (!await conn.DatabaseExists(database))
            {
                await new DatabaseSpecification().BuildDatabase(conn, database);
            }

            builder.Database = database;
            connectionStrings.Add(builder.ConnectionString);
        }

        await conn.CloseAsync();

        // Pipeline one: the real Marten store advertising per-(database, tenant) projection agents.
        _martenStore = DocumentStore.For(opts =>
        {
            opts.DatabaseSchemaName = "affinity";
            opts.AutoCreateSchemaObjects = AutoCreate.None;

            opts.Events.TenancyStyle = JasperFx.MultiTenancy.TenancyStyle.Conjoined;
            opts.Events.UseTenantPartitionedEvents = true;
            opts.Events.UseArchivedStreamPartitioning = false;

            opts.MultiTenantedDatabases(tenancy =>
            {
                for (var i = 0; i < connectionStrings.Count; i++)
                {
                    tenancy.AddMultipleTenantDatabase(connectionStrings[i], _databases[i])
                        .ForTenants(_tenantsPerDatabase.Select(t => $"{_databases[i]}-{t}").ToArray());
                }
            });

            opts.Schema.For<BlueGreenTrip>().MultiTenanted();
            opts.Projections.Add(new TripProjection { Version = 2 }, ProjectionLifecycle.Async);
        });

        await using var family = new EventSubscriptionAgentFamily([_martenStore], []);
        _projectionAgents = await family.SupportedAgentsAsync();

        // Pipeline two: a real Wolverine Postgres message store per tenant database, whose Uri is the
        // wolverinedb:// identity the durability family distributes.
        foreach (var connectionString in connectionStrings)
        {
            var settings = new DatabaseSettings
            {
                ConnectionString = connectionString,
                Role = MessageStoreRole.Tenant,
                SchemaName = "wolverine"
            };

            _messageStores.Add(new PostgresqlMessageStore(settings, new DurabilitySettings(),
                NpgsqlDataSource.Create(connectionString), NullLogger<PostgresqlMessageStore>.Instance));
        }
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var store in _messageStores)
        {
            await store.DisposeAsync();
        }

        _martenStore.Dispose();
    }

    [Fact]
    public void the_real_uri_pipelines_join_and_every_database_co_locates()
    {
        _projectionAgents.Count.ShouldBe(_databases.Length * _tenantsPerDatabase.Length,
            "an agent per (database, tenant) is the shape that makes this worth testing");

        var grid = new AssignmentGrid();
        grid.WithNode(1, Guid.NewGuid());
        grid.WithNode(2, Guid.NewGuid());

        var durabilityAgents = _messageStores.Select(x => x.Uri).ToArray();
        grid.WithAgents(_projectionAgents.Concat(durabilityAgents).ToArray());

        // The two passes in the order NodeAgentController guarantees, both over real URIs.
        grid.DistributeByGroupAffinity(EventSubscriptionAgentFamily.SchemeName,
            EventSubscriptionAgentFamily.DatabaseKeyOf);
        var preference = DurabilityProjectionAffinity.BuildPreference(grid);
        grid.DistributeEvenlyWithAffinity(PersistenceConstants.AgentScheme, preference.NodeFor);

        // GH-3785: the join is fail-silent at runtime, so the integration test that proves the two URI
        // pipelines actually agree is exactly the place to pin the diagnostic that reports it. Every one
        // of these real databases must have matched -- a miss here is the spelling divergence this whole
        // test exists to catch, and it would otherwise look identical to success.
        preference.KnownDatabases.ShouldBe(_databases.Length);
        preference.Considered.ShouldBe(_messageStores.Count);
        preference.Matched.ShouldBe(_messageStores.Count);

        grid.AllAgents.ShouldAllBe(a => a.AssignedNode != null);

        for (var i = 0; i < _databases.Length; i++)
        {
            var database = _databases[i];
            var projectionOwners = _projectionAgents
                .Where(uri => EventSubscriptionAgentFamily.DatabaseIdOf(uri)?.Name == database)
                .Select(uri => grid.AgentFor(uri).AssignedNode)
                .Distinct()
                .ToList();

            projectionOwners.ShouldHaveSingleItem($"{database}'s projection agents must be on one node");

            grid.AgentFor(durabilityAgents[i]).AssignedNode.ShouldBe(projectionOwners.Single(),
                $"{database}'s durability agent must land with its projections — if this fails the two " +
                "families' database spellings have diverged and the affinity join is silently dead");
        }
    }
}
