using IntegrationTests;
using JasperFx.Resources;
using Microsoft.Extensions.DependencyInjection;
using MySqlConnector;
using Shouldly;
using Wolverine;
using Wolverine.ComplianceTests;
using Wolverine.MySql;
using Wolverine.MySql.Transport;
using Wolverine.Persistence.Durability;
using Wolverine.Runtime;
using Wolverine.Tracking;
using Xunit;

namespace MySqlTests.MultiTenancy;

/// <summary>
/// The MySQL twin of GH-4455. Tenants registered by <see cref="MySqlDataSource"/> rather than by
/// connection string went through <c>buildTenantStoreForDataSource</c>, which — unlike its connection
/// string sibling — never set <c>store.Name</c>, so every one of them kept the
/// <c>TransportConstants.Default</c> name, "default".
///
/// Both <see cref="MultiTenantedQueueListener"/> and <see cref="MultiTenantedQueueSender"/> cache per
/// tenant database BY that name. The sender keys its map by tenant id AND by store name, with a branch
/// for "this database has already been seen under a different tenant id" — so with every store answering
/// to "default", the second tenant hit the first tenant's entry and its messages were written into the
/// first tenant's database.
/// </summary>
[Collection("mysql")]
public class tenants_registered_by_data_source : MySqlMultiTenancyContext
{
    private const string SchemaName = "tenant_by_datasource";
    private const string QueueName = "bysource4455";

    private const string QueueTableName = $"wolverine_queue_{QueueName}";

    private readonly string[] theTenants = ["red", "blue", "green"];

    protected override void configureWolverine(WolverineOptions opts)
    {
        opts.PersistMessagesWithMySql(Servers.MySqlConnectionString, SchemaName)
            .EnableMessageTransport()

            // The path under test: tenants handed over as data sources, not connection strings
            .RegisterStaticTenantsByDataSource(tenants =>
            {
                tenants.Register("red", new MySqlDataSourceBuilder(tenant1ConnectionString).Build());
                tenants.Register("blue", new MySqlDataSourceBuilder(tenant2ConnectionString).Build());
                tenants.Register("green", new MySqlDataSourceBuilder(tenant3ConnectionString).Build());
            });

        // Subscriber only -- this host never listens to the queue, so nothing drains it out from under
        // the assertions
        opts.PublishAllMessages().ToMySqlQueue(QueueName);

        opts.Services.AddResourceSetupOnStartup();
        opts.Discovery.DisableConventionalDiscovery();
    }

    protected override async Task onStartup()
    {
        foreach (var tenantId in theTenants)
        {
            await storeForAsync(tenantId);
        }

        foreach (var connectionString in allConnectionStrings())
        {
            await using var conn = new MySqlConnection(connectionString);
            await conn.OpenAsync();
            try
            {
                await using var cmd = conn.CreateCommand();
                cmd.CommandText = $"delete from {QueueTableName}";
                await cmd.ExecuteNonQueryAsync();
            }
            catch (MySqlException)
            {
                // Nothing provisioned in this database yet, nothing to clean
            }
            finally
            {
                await conn.CloseAsync();
            }
        }
    }

    private string[] allConnectionStrings() =>
    [
        Servers.MySqlConnectionString, tenant1ConnectionString, tenant2ConnectionString, tenant3ConnectionString
    ];

    private string connectionStringFor(string tenantId) => tenantId switch
    {
        "red" => tenant1ConnectionString,
        "blue" => tenant2ConnectionString,
        "green" => tenant3ConnectionString,
        _ => throw new ArgumentOutOfRangeException(nameof(tenantId))
    };

    private async Task<IMessageStore> storeForAsync(string tenantId)
    {
        var stores = (MultiTenantedMessageStore)theHost.GetRuntime().Storage;
        return await stores.GetDatabaseAsync(tenantId);
    }

    private MySqlQueue theQueue =>
        theHost.GetRuntime().Options.Transports.OfType<MySqlTransport>().Single().Queues[QueueName];

    /// <summary>
    /// The structural claim: a data-source-registered tenant has to be named like a connection-string one,
    /// because that name is the cache key the sender and the listener both index by.
    /// </summary>
    [Fact]
    public async Task every_tenant_database_has_a_distinct_name()
    {
        var names = new List<string>();
        foreach (var tenantId in theTenants)
        {
            var store = await storeForAsync(tenantId);
            store.Name.ShouldNotBe("default", $"Tenant '{tenantId}' kept the default store name");
            names.Add(store.Name);
        }

        names.Distinct().Count().ShouldBe(theTenants.Length);
    }

    /// <summary>
    /// The behavioural claim: three tenants, three databases, one row each.
    ///
    /// The shape matters. It has to send for more than one tenant through the SAME queue endpoint — the
    /// first tenant seen always routes correctly, and it is the second that collides with the first's cache
    /// entry. That in turn only holds now that <c>buildSenderIfMissing</c> keeps the sender it builds;
    /// before that fix each send got a brand new <see cref="MultiTenantedQueueSender"/> with an empty cache,
    /// so every send looked like the first and the collision was invisible.
    /// </summary>
    [Fact]
    public async Task each_tenant_gets_exactly_its_own_message()
    {
        foreach (var tenantId in theTenants)
        {
            var envelope = ObjectMother.Envelope();
            envelope.TenantId = tenantId;
            envelope.DeliverBy = DateTimeOffset.UtcNow.AddHours(1);
            await theQueue.SendAsync(envelope);
        }

        foreach (var tenantId in theTenants)
        {
            (await rowCountAsync(connectionStringFor(tenantId)))
                .ShouldBe(1, $"Expected exactly one row in the '{tenantId}' database");
        }

        (await rowCountAsync(Servers.MySqlConnectionString)).ShouldBe(0);
    }

    private static async Task<long> rowCountAsync(string connectionString)
    {
        await using var conn = new MySqlConnection(connectionString);
        await conn.OpenAsync();
        try
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = $"select count(*) from {QueueTableName}";
            return Convert.ToInt64(await cmd.ExecuteScalarAsync());
        }
        finally
        {
            await conn.CloseAsync();
        }
    }
}

