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
using Wolverine.RDBMS;
using Wolverine.SqlServer;
using Wolverine.Tracking;
using Wolverine.Transports.Stub;

namespace EfCoreTests.MultiTenancy;

/// <summary>
/// GH-5016. DurabilityMode.ServerlessWithOutbox with multi-tenanted message databases: a tenant's outgoing
/// envelopes are persisted in that tenant's database as owned by any node (0) and sent inline after the commit,
/// and one durability pass works through every tenant database, not just the main one.
/// </summary>
[Collection("multi-tenancy")]
public abstract class serverless_with_outbox_multi_tenancy_5016 : IAsyncLifetime
{
    private const string SchemaName = "serverless_tenancy";
    private static readonly string[] Tenants = ["red", "blue", "green"];

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private IHost theHost = null!;

    protected abstract void configureStorage(WolverineOptions opts, string schemaName);

    protected abstract void clearPools();

    public async ValueTask InitializeAsync()
    {
        theHost = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Durability.Mode = DurabilityMode.ServerlessWithOutbox;
                opts.Discovery.DisableConventionalDiscovery()
                    .IncludeType(typeof(EfCoreServerlessTenantedOutboxHandler));

                configureStorage(opts, SchemaName);

                opts.UseEntityFrameworkCoreTransactions();
                opts.Policies.AutoApplyTransactions();

                opts.PublishMessage<EfCoreServerlessTenantedCascaded>().To("stub://efcore-serverless-tenanted");

                opts.Services.AddResourceSetupOnStartup(StartupAction.ResetState);
            }).StartAsync();

        await theHost.ResetResourceState();
    }

    public async ValueTask DisposeAsync()
    {
        await theHost.StopAsync();
        theHost.Dispose();

        clearPools();
    }

    private IReadOnlyList<Envelope> sent()
    {
        return theHost.GetRuntime().Options.Transports.GetOrCreate<StubTransport>()
            .Endpoints["efcore-serverless-tenanted"].Sent.ToArray();
    }

    private async Task<IMessageStore> tenantStoreAsync(string tenantId)
    {
        var tenanted = theHost.GetRuntime().Stores.MultiTenanted.Single();
        return await tenanted.GetDatabaseAsync(tenantId);
    }

    private async Task<IReadOnlyList<Envelope>> outboxRowsAsync(string tenantId)
    {
        var store = await tenantStoreAsync(tenantId);
        return await store.Admin.AllOutgoingAsync();
    }

    [Fact]
    public async Task cascaded_messages_are_sent_inline_from_the_tenant_database()
    {
        var id = Guid.NewGuid();

        await theHost.MessageBus().InvokeForTenantAsync("blue", new EfCoreServerlessTenantedCommand(id), Token);

        // The handler's entity went to the blue database...
        var factory = theHost.Services.GetRequiredService<IDbContextOutboxFactory>();
        var outbox = await factory.CreateForTenantAsync<ItemsDbContext>("blue", Token);
        (await outbox.DbContext.Items.FindAsync([id], Token)).ShouldNotBeNull();

        // ...and its cascaded message went out before InvokeAsync() returned, with nothing left behind
        // in any tenant's outbox
        sent().Single().Message.ShouldBe(new EfCoreServerlessTenantedCascaded(id));

        foreach (var tenantId in Tenants)
        {
            (await outboxRowsAsync(tenantId)).ShouldBeEmpty();
        }
    }

    [Fact]
    public async Task one_durability_pass_recovers_unsent_rows_from_every_tenant_database()
    {
        var factory = theHost.Services.GetRequiredService<IDbContextOutboxFactory>();

        // Persisted through each tenant's DbContext outbox, but never sent, as when the request died after
        // the commit and before the inline send
        var expected = new Dictionary<string, Guid>();
        foreach (var tenantId in Tenants)
        {
            var id = Guid.NewGuid();
            var outbox = await factory.CreateForTenantAsync<ItemsDbContext>(tenantId, Token);

            outbox.DbContext.Items.Add(new Item { Id = id, Name = tenantId });
            await outbox.PublishAsync(new EfCoreServerlessTenantedCascaded(id));

            await outbox.DbContext.SaveChangesAsync(Token);

            var row = (await outboxRowsAsync(tenantId)).Single();
            row.OwnerId.ShouldBe(0);

            expected[tenantId] = row.Id;
        }

        // Still within OutboxStaleTime, so a pass leaves every one of them to the request that may still be
        // sending it
        var early = await theHost.RunDurabilityPassAsync(30.Seconds(), Token);
        early.OutgoingSent.ShouldBe(0);
        sent().ShouldBeEmpty();

        foreach (var (tenantId, rowId) in expected)
        {
            var outbox = await factory.CreateForTenantAsync<ItemsDbContext>(tenantId, Token);
            var sql =
                $"update {SchemaName}.{DatabaseConstants.OutgoingTable} set \"timestamp\" = {{0}} where id = {{1}}";
            await outbox.DbContext.Database.ExecuteSqlRawAsync(sql,
                [DateTimeOffset.UtcNow.Subtract(2.Hours()), rowId], Token);
        }

        var report = await theHost.RunDurabilityPassAsync(30.Seconds(), Token);

        report.OutgoingSent.ShouldBe(Tenants.Length);
        report.FailedSteps.ShouldBe(0);

        // The pass took each tenant database's own lock and sent that tenant's row
        foreach (var (tenantId, rowId) in expected)
        {
            var store = await tenantStoreAsync(tenantId);
            var storeReport = report.Stores.Single(x => x.StoreUri == store.Uri);

            storeReport.Lock.ShouldBe(DurabilityPassLockStatus.Acquired);
            storeReport.Steps.Sum(x => x.Result.OutgoingSent).ShouldBe(1);

            (await outboxRowsAsync(tenantId)).ShouldBeEmpty();
        }

        sent().Select(x => x.Id).OrderBy(x => x).ShouldBe(expected.Values.OrderBy(x => x));
    }
}

public class serverless_with_outbox_multi_tenancy_5016_on_postgresql : serverless_with_outbox_multi_tenancy_5016
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

    protected override void clearPools()
    {
        NpgsqlConnection.ClearAllPools();
    }
}

// Dynamic tenants, so the pass finds the tenant databases by refreshing the master table list
public class serverless_with_outbox_multi_tenancy_5016_on_postgresql_master_table : serverless_with_outbox_multi_tenancy_5016
{
    protected override void configureStorage(WolverineOptions opts, string schemaName)
    {
        var builder = new NpgsqlConnectionStringBuilder(Servers.PostgresConnectionString);

        opts.PersistMessagesWithPostgresql(Servers.PostgresConnectionString, schemaName)
            .UseMasterTableTenancy(tenants =>
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

    protected override void clearPools()
    {
        NpgsqlConnection.ClearAllPools();
    }
}

public class serverless_with_outbox_multi_tenancy_5016_on_sqlserver : serverless_with_outbox_multi_tenancy_5016
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

    protected override void clearPools()
    {
        SqlConnection.ClearAllPools();
    }
}

public record EfCoreServerlessTenantedCommand(Guid Id);

public record EfCoreServerlessTenantedCascaded(Guid Id);

public static class EfCoreServerlessTenantedOutboxHandler
{
    public static EfCoreServerlessTenantedCascaded Handle(EfCoreServerlessTenantedCommand command,
        ItemsDbContext dbContext)
    {
        dbContext.Items.Add(new Item { Id = command.Id, Name = "Serverless" });
        return new EfCoreServerlessTenantedCascaded(command.Id);
    }
}
