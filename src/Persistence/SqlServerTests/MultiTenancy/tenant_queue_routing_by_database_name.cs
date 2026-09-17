using IntegrationTests;
using JasperFx.Resources;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Wolverine;
using Wolverine.ComplianceTests;
using Wolverine.Persistence.Durability;
using Wolverine.Runtime;
using Wolverine.SqlServer;
using Wolverine.SqlServer.Transport;
using Wolverine.Tracking;
using Xunit;

namespace SqlServerTests.MultiTenancy;

/// <summary>
/// The SQL Server twin of GH-4455. <see cref="MultiTenantedQueueSender"/> and
/// <see cref="MultiTenantedQueueListener"/> both cache per tenant database BY <c>IMessageStore.Name</c>,
/// and <c>SqlServerTenantedMessageStore.buildTenantStoreForConnectionString</c> never set it -- so every
/// tenant store kept the <c>TransportConstants.Default</c> name, "default".
///
/// The sender is the damaging half. Its <c>_byDatabase</c> map is keyed by BOTH the tenant id and the
/// store name, with a branch for "this database has been encountered before under a different tenant id".
/// With every store answering to "default" that branch fired for every tenant after the first, so the
/// second tenant's messages were written into the FIRST tenant's database.
/// </summary>
public class tenant_queue_routing_by_database_name : MultiTenancyContext
{
    private const string QueueName = "pertenant4455";
    private const string SchemaName = "tq4455";

    private const string QueueTableName = $"wolverine_queue_{QueueName}";

    protected override void configureWolverine(WolverineOptions opts)
    {
        opts.UseSqlServerPersistenceAndTransport(Servers.SqlServerConnectionString, SchemaName, SchemaName)
            .AutoProvision();

        opts.PersistMessagesWithSqlServer(Servers.SqlServerConnectionString, SchemaName)
            .RegisterStaticTenants(tenants =>
            {
                tenants.Register("red", tenant1ConnectionString);
                tenants.Register("blue", tenant2ConnectionString);
                tenants.Register("green", tenant3ConnectionString);
            });

        // Subscriber only -- nothing drains the queue out from under the assertions
        opts.PublishAllMessages().ToSqlServerQueue(QueueName);

        opts.Services.AddResourceSetupOnStartup();
        opts.Discovery.DisableConventionalDiscovery();
    }

    protected override async Task onStartup()
    {
        // Materialize every tenant store so the queue tables exist in all of them and a zero row count
        // below is a real measurement rather than a missing table
        foreach (var tenantId in theTenants)
        {
            await storeForAsync(tenantId);
        }

        foreach (var connectionString in allConnectionStrings())
        {
            await using var conn = new SqlConnection(connectionString);
            await conn.OpenAsync();
            try
            {
                await using var cmd = conn.CreateCommand();
                cmd.CommandText = $"delete from {SchemaName}.{QueueTableName}";
                await cmd.ExecuteNonQueryAsync();
            }
            catch (SqlException)
            {
                // Nothing provisioned in this database yet, nothing to clean
            }
            finally
            {
                await conn.CloseAsync();
            }
        }
    }

    private readonly string[] theTenants = ["red", "blue", "green"];

    private string[] allConnectionStrings() =>
    [
        Servers.SqlServerConnectionString, tenant1ConnectionString, tenant2ConnectionString,
        tenant3ConnectionString
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

    private SqlServerQueue theQueue =>
        theHost.GetRuntime().Options.Transports.OfType<SqlServerTransport>().Single().Queues[QueueName];

    /// <summary>
    /// Deliberately the token-less overload. SqlServerQueue also carries a SendAsync(Envelope,
    /// CancellationToken) that opens its own connection against the PARENT connection string -- that one
    /// bypasses the multi-tenanted sender entirely, which is exactly the thing under test here.
    /// </summary>
#pragma warning disable xUnit1051
    private ValueTask sendAsync(Envelope envelope) => theQueue.SendAsync(envelope);
#pragma warning restore xUnit1051

    /// <summary>
    /// The structural claim: a tenant database's name has to tell it apart from every other one, because
    /// that name is the cache key both the sender and the listener index by.
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
    /// The behavioural claim, and the one that actually matters: three tenants, three databases, one row
    /// each. Note that this has to send for more than one tenant through the SAME host to reproduce -- the
    /// first tenant seen always routes correctly, and it is the SECOND one that collides with the first
    /// tenant's cache entry and writes into its database. A one-tenant-per-host assertion passes either way.
    /// </summary>
    [Fact]
    public async Task each_tenant_gets_exactly_its_own_message()
    {
        foreach (var tenantId in theTenants)
        {
            var envelope = ObjectMother.Envelope();
            envelope.TenantId = tenantId;
            envelope.DeliverBy = DateTimeOffset.UtcNow.AddHours(1);
            await sendAsync(envelope);
        }

        foreach (var tenantId in theTenants)
        {
            (await rowCountAsync(connectionStringFor(tenantId)))
                .ShouldBe(1, $"Expected exactly one row in the '{tenantId}' database");
        }

        (await rowCountAsync(Servers.SqlServerConnectionString)).ShouldBe(0);
    }

    private static async Task<long> rowCountAsync(string connectionString)
    {
        await using var conn = new SqlConnection(connectionString);
        await conn.OpenAsync();
        try
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = $"select count(*) from {SchemaName}.{QueueTableName}";
            return Convert.ToInt64(await cmd.ExecuteScalarAsync());
        }
        finally
        {
            await conn.CloseAsync();
        }
    }
}
