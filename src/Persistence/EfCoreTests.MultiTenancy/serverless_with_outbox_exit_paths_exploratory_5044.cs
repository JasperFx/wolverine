using System.Collections.Concurrent;
using IntegrationTests;
using JasperFx;
using JasperFx.Core;
using JasperFx.Resources;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Npgsql;
using SharedPersistenceModels.Items;
using Shouldly;
using Wolverine;
using Wolverine.EntityFrameworkCore;
using Wolverine.Persistence.Durability;
using Wolverine.Postgresql;
using Wolverine.Runtime;
using Wolverine.SqlServer;
using Wolverine.Tracking;
using Wolverine.Transports;
using Wolverine.Transports.Tcp;
using Wolverine.Util;

namespace EfCoreTests.MultiTenancy;

/// <summary>
/// Exploratory tests for PR #5044 (DurabilityMode.ServerlessWithOutbox) with database-per-tenant message storage,
/// over TCP so a real DurableSendingAgent is in play: a tenant handler's cascade, a root IMessageBus publish
/// tagged with a tenant id outside of any handler, and a tenant IDbContextOutbox. A failed send must leave the row
/// in the TENANT database owned by any node (0), and one pass must deliver it.
/// </summary>
[Collection("multi-tenancy")]
public abstract class serverless_with_outbox_exit_paths_exploratory_5044 : IAsyncDisposable
{
    private const string SchemaName = "explore5044_exit_tenancy";
    private static readonly string[] Tenants = ["red", "blue", "green"];

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private readonly int thePort = PortFinder.GetAvailablePort();
    private IHost? theFunction;
    private IHost? theReceiver;

    protected abstract void configureStorage(WolverineOptions opts, string schemaName);

    protected abstract void clearPools();

    public async ValueTask DisposeAsync()
    {
        if (theFunction != null)
        {
            await theFunction.StopAsync();
            theFunction.Dispose();
        }

        if (theReceiver != null)
        {
            await theReceiver.StopAsync();
            theReceiver.Dispose();
        }

        clearPools();
    }

    private async Task startTheFunctionAsync()
    {
        theFunction = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Durability.Mode = DurabilityMode.ServerlessWithOutbox;
                opts.Durability.OutboxStaleTime = 100.Milliseconds();

                opts.Discovery.DisableConventionalDiscovery()
                    .IncludeType(typeof(Explore5044TenantedHandler));

                configureStorage(opts, SchemaName);

                opts.UseEntityFrameworkCoreTransactions();
                opts.Policies.AutoApplyTransactions();

                opts.PublishMessage<Explore5044TenantedCascaded>().ToPort(thePort);

                opts.Services.AddResourceSetupOnStartup(StartupAction.ResetState);
            }).StartAsync(Token);

        await theFunction.ResetResourceState();
    }

    private async Task startTheReceiverAsync()
    {
        theReceiver = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Durability.Mode = DurabilityMode.Solo;
                opts.Discovery.DisableConventionalDiscovery().IncludeType(typeof(Explore5044TenantedReceiver));
                opts.ListenAtPort(thePort);
            }).StartAsync(Token);
    }

    private async Task<IReadOnlyList<Envelope>> outboxRowsAsync(string? tenantId)
    {
        var runtime = theFunction!.GetRuntime();
        if (tenantId == null) return await runtime.Stores.MultiTenanted.Single().Main.Admin.AllOutgoingAsync();

        var store = await runtime.Stores.MultiTenanted.Single().GetDatabaseAsync(tenantId);
        return await store.Admin.AllOutgoingAsync();
    }

    private async Task<Dictionary<string, IReadOnlyList<Envelope>>> allOutboxRowsAsync()
    {
        var all = new Dictionary<string, IReadOnlyList<Envelope>> { ["*main*"] = await outboxRowsAsync(null) };
        foreach (var tenantId in Tenants) all[tenantId] = await outboxRowsAsync(tenantId);
        return all;
    }

    private static async Task shouldBeReceivedAsync(Guid id)
    {
        var timeout = DateTimeOffset.UtcNow.Add(10.Seconds());
        while (!Explore5044TenantedReceiver.Received.ContainsKey(id))
        {
            if (DateTimeOffset.UtcNow > timeout) throw new TimeoutException($"Message {id} never reached the receiver");
            await Task.Delay(50, Token);
        }
    }

    private async Task assertDeliveredAsync(Guid id, string tenantId, bool receiverIsUp)
    {
        var all = await allOutboxRowsAsync();
        Console.WriteLine($"[explore5044 tenancy] outbox rows after the call: {string.Join(", ", all.Select(x => $"{x.Key}={x.Value.Count}"))}");

        if (receiverIsUp)
        {
            all.Values.SelectMany(x => x).ShouldBeEmpty();
            await shouldBeReceivedAsync(id);
            return;
        }

        // In the tenant's own database, owned by any node, and nowhere else
        all[tenantId].Count.ShouldBe(1);
        all[tenantId].Single().OwnerId.ShouldBe(TransportConstants.AnyNode);
        all.Where(x => x.Key != tenantId).SelectMany(x => x.Value).ShouldBeEmpty();

        await startTheReceiverAsync();
        await Task.Delay(200, Token);
        await theFunction!.RunDurabilityPassAsync(token: Token);

        await shouldBeReceivedAsync(id);
        (await allOutboxRowsAsync()).Values.SelectMany(x => x).ShouldBeEmpty();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task tenant_handler_cascade(bool receiverIsUp)
    {
        if (receiverIsUp) await startTheReceiverAsync();
        await startTheFunctionAsync();

        var id = Guid.NewGuid();
        await theFunction!.MessageBus().InvokeForTenantAsync("blue", new Explore5044TenantedCommand(id), Token);

        await assertDeliveredAsync(id, "blue", receiverIsUp);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task root_bus_publish_tagged_with_a_tenant(bool receiverIsUp)
    {
        if (receiverIsUp) await startTheReceiverAsync();
        await startTheFunctionAsync();

        var id = Guid.NewGuid();
        using (var scope = theFunction!.Services.CreateScope())
        {
            var bus = scope.ServiceProvider.GetRequiredService<IMessageBus>();
            await bus.PublishAsync(new Explore5044TenantedCascaded(id), new DeliveryOptions { TenantId = "green" });
        }

        await assertDeliveredAsync(id, "green", receiverIsUp);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task tenant_dbcontext_outbox(bool receiverIsUp)
    {
        if (receiverIsUp) await startTheReceiverAsync();
        await startTheFunctionAsync();

        var id = Guid.NewGuid();
        var factory = theFunction!.Services.GetRequiredService<IDbContextOutboxFactory>();
        var outbox = await factory.CreateForTenantAsync<ItemsDbContext>("red", Token);
        outbox.DbContext.Items.Add(new Item { Id = id, Name = "explore5044" });
        await outbox.PublishAsync(new Explore5044TenantedCascaded(id));
        await outbox.SaveChangesAndFlushMessagesAsync(Token);

        await assertDeliveredAsync(id, "red", receiverIsUp);
    }
}

public class serverless_with_outbox_exit_paths_exploratory_5044_on_postgresql
    : serverless_with_outbox_exit_paths_exploratory_5044
{
    protected override void configureStorage(WolverineOptions opts, string schemaName)
    {
        var builder = new NpgsqlConnectionStringBuilder(Servers.PostgresConnectionString);

        opts.PersistMessagesWithPostgresql(Servers.PostgresConnectionString, schemaName)
            .RegisterStaticTenants(tenants =>
            {
                builder.Database = "db1";
                tenants.Register("red", builder.ConnectionString);

                builder.Database = "db2";
                tenants.Register("blue", builder.ConnectionString);

                builder.Database = "db3";
                tenants.Register("green", builder.ConnectionString);
            });

        opts.Services.AddDbContextWithWolverineManagedMultiTenancy<ItemsDbContext>((b, connectionString, _) =>
        {
            b.UseNpgsql(connectionString.Value, x => x.MigrationsAssembly("MultiTenantedEfCoreWithPostgreSQL"));
        }, AutoCreate.CreateOrUpdate);
    }

    protected override void clearPools() => NpgsqlConnection.ClearAllPools();
}

public class serverless_with_outbox_exit_paths_exploratory_5044_on_sqlserver
    : serverless_with_outbox_exit_paths_exploratory_5044
{
    protected override void configureStorage(WolverineOptions opts, string schemaName)
    {
        var builder = new SqlConnectionStringBuilder(Servers.SqlServerConnectionString);

        opts.PersistMessagesWithSqlServer(Servers.SqlServerConnectionString, schemaName)
            .RegisterStaticTenants(tenants =>
            {
                builder.InitialCatalog = "db1";
                tenants.Register("red", builder.ConnectionString);

                builder.InitialCatalog = "db2";
                tenants.Register("blue", builder.ConnectionString);

                builder.InitialCatalog = "db3";
                tenants.Register("green", builder.ConnectionString);
            });

        opts.Services.AddDbContextWithWolverineManagedMultiTenancy<ItemsDbContext>((b, connectionString, _) =>
        {
            b.UseSqlServer(connectionString.Value, x => x.MigrationsAssembly("MultiTenantedEfCoreWithSqlServer"));
        }, AutoCreate.CreateOrUpdate);
    }

    protected override void clearPools() => SqlConnection.ClearAllPools();
}

public record Explore5044TenantedCommand(Guid Id);

public record Explore5044TenantedCascaded(Guid Id);

public static class Explore5044TenantedHandler
{
    public static Explore5044TenantedCascaded Handle(Explore5044TenantedCommand command, ItemsDbContext dbContext)
    {
        dbContext.Items.Add(new Item { Id = command.Id, Name = "explore5044" });
        return new Explore5044TenantedCascaded(command.Id);
    }
}

public static class Explore5044TenantedReceiver
{
    public static readonly ConcurrentDictionary<Guid, bool> Received = new();

    public static void Handle(Explore5044TenantedCascaded message) => Received[message.Id] = true;
}
