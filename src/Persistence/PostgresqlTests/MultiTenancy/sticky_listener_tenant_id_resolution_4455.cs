using IntegrationTests;
using JasperFx.Core;
using JasperFx.Descriptors;
using JasperFx.MultiTenancy;
using JasperFx.Resources;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Npgsql;
using Shouldly;
using Weasel.Postgresql;
using Weasel.Postgresql.Migrations;
using Wolverine;
using Wolverine.Postgresql;
using Wolverine.Postgresql.Transport;
using Xunit;

namespace PostgresqlTests.MultiTenancy;

/// <summary>
/// GH-4455. The sticky, per-tenant-database queue listener agents encode their identity as
/// "pg-queue-listener://{queue}/{database name}", then resolve the database back out of that Uri by
/// handing the name to <c>MultiTenantedMessageStore.GetDatabaseAsync</c> -- which only understands
/// TENANT IDS. Every application whose tenant source is keyed by something other than the physical
/// database name (a Key Vault secret per tenant, say) therefore had the agent ask its source for a
/// tenant that does not exist, and the agent failed to start, forever, on a retry loop.
/// </summary>
public class sticky_listener_tenant_id_resolution_4455 : PostgresqlContext, IAsyncLifetime
{
    private const string QueueName = "sticky4455";

    private readonly string theSuffix = Guid.NewGuid().ToString("N")[..8];
    private readonly string[] theTenants = ["tenanta", "tenantb"];

    private RecordingTenantSource theTenantSource = null!;
    private IHost _host = null!;

    private string MainSchema => $"sticky4455_{theSuffix}";
    private string TenantDatabase(string tenant) => $"w4455_{tenant}_{theSuffix}";

    private static string ConnectionStringFor(string database)
    {
        return new NpgsqlConnectionStringBuilder(Servers.PostgresConnectionString)
        {
            Database = database
        }.ConnectionString;
    }

    public async ValueTask InitializeAsync()
    {
        await using (var conn = new NpgsqlConnection(Servers.PostgresConnectionString))
        {
            await conn.OpenAsync();

            foreach (var tenant in theTenants)
            {
                var databaseName = TenantDatabase(tenant);
                if (!await conn.DatabaseExists(databaseName))
                {
                    await new DatabaseSpecification().BuildDatabase(conn, databaseName);
                }
            }

            await conn.CloseAsync();
        }

        // Deliberately NOT keyed by the physical database name -- that is the whole point of the defect
        theTenantSource = new RecordingTenantSource(theTenants
            .ToDictionary(x => x, x => ConnectionStringFor(TenantDatabase(x))));

        _host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Durability.Mode = DurabilityMode.Solo;

                opts.PersistMessagesWithPostgresql(ConnectionStringFor("postgres"), MainSchema)
                    .EnableMessageTransport(transport => transport.TransportSchemaName(MainSchema))
                    .RegisterTenants(theTenantSource);

                opts.Services.AddResourceSetupOnStartup();

                opts.Discovery.DisableConventionalDiscovery();

                // ExclusiveNode is what puts this queue behind the sticky per-tenant listener agents
                opts.ListenToPostgresqlQueue(QueueName).ExclusiveNodeWithParallelism(1);
            }).StartAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await _host.StopAsync();
        _host.Dispose();
    }

    [Fact]
    public async Task the_sticky_listener_agents_start_for_every_tenant_database()
    {
        // One per tenant database, plus one for the main database
        var expected = theTenants.Length + 1;

        var started = await _host.WaitUntilAssignmentsChangeTo(w =>
        {
            w.AgentScheme = StickyPostgresqlQueueListenerAgentFamily.StickyListenerSchema;
            w.ExpectRunningAgents(_host, expected);
        }, 30.Seconds());

        started.ShouldBeTrue(
            $"Expected {expected} sticky listener agents, but only saw " +
            $"{_host.RunningAgents().Count(x => x.Scheme == StickyPostgresqlQueueListenerAgentFamily.StickyListenerSchema)}. " +
            $"Tenant ids asked of the tenant source: {theTenantSource.Requested.Join(", ")}");

        // ...and the tenant source was never asked for anything but a real tenant id
        theTenantSource.Unknown.ShouldBeEmpty();
    }
}

/// <summary>
/// Stands in for the Key Vault / Infisical backed <see cref="ITenantedSource{T}"/> in GH-4455: it knows
/// tenant ids and nothing else, and it records every id it is asked about so a test can prove what
/// Wolverine actually handed it.
/// </summary>
internal class RecordingTenantSource : ITenantedSource<string>
{
    private readonly Dictionary<string, string> _connectionStrings;

    public RecordingTenantSource(Dictionary<string, string> connectionStrings)
    {
        _connectionStrings = connectionStrings;
    }

    public List<string> Requested { get; } = new();

    public IEnumerable<string> Unknown => Requested.Where(x => !_connectionStrings.ContainsKey(x)).Distinct();

    public DatabaseCardinality Cardinality => DatabaseCardinality.DynamicMultiple;

    public ValueTask<string> FindAsync(string tenantId)
    {
        lock (Requested)
        {
            Requested.Add(tenantId);
        }

        if (!_connectionStrings.TryGetValue(tenantId, out var connectionString))
        {
            throw new UnknownTenantIdException(tenantId);
        }

        return new ValueTask<string>(connectionString);
    }

    public Task RefreshAsync() => Task.CompletedTask;

    public IReadOnlyList<string> AllActive() => _connectionStrings.Values.ToList();

    public IReadOnlyList<Assignment<string>> AllActiveByTenant()
        => _connectionStrings.Select(pair => new Assignment<string>(pair.Key, pair.Value)).ToList();
}
