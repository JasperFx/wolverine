using IntegrationTests;
using JasperFx;
using JasperFx.Core;
using JasperFx.Resources;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine;
using Wolverine.EntityFrameworkCore;
using Wolverine.EntityFrameworkCore.Internals;
using Wolverine.Persistence.Sagas;
using Wolverine.Postgresql;
using Wolverine.SqlServer;
using Wolverine.Tracking;

namespace EfCoreTests.MultiTenancy.ConjoinedTenancy;

/// <summary>
///     Compliance battery for conjoined EF Core multi-tenancy (GH-3462). The assertions
///     mirror Marten's conjoined tenancy semantics: default tenant sentinel, stamp on
///     insert, tenant-bound query filtering, tenant-scoped saga loads, and cross-tenant
///     write rejection
/// </summary>
[Collection("multi-tenancy")]
public abstract class ConjoinedTenancyCompliance : IAsyncLifetime
{
    private readonly DatabaseEngine _engine;
    protected IDbContextBuilder<ConjoinedItemsDbContext> theBuilder = null!;
    protected IHost theHost = null!;

    protected ConjoinedTenancyCompliance(DatabaseEngine engine)
    {
        _engine = engine;
    }

    public async Task InitializeAsync()
    {
        theHost = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Durability.Mode = DurabilityMode.Solo;

                opts.Discovery.DisableConventionalDiscovery()
                    .IncludeType<ConjoinedItemHandler>()
                    .IncludeType<ConjoinedCounterSaga>();

                if (_engine == DatabaseEngine.PostgreSQL)
                {
                    opts.PersistMessagesWithPostgresql(Servers.PostgresConnectionString, "conjoined_wolverine");
                    opts.Services.AddDbContextWithWolverineManagedConjoinedTenancy<ConjoinedItemsDbContext>(
                        (builder, connectionString) => builder.UseNpgsql(connectionString.Value),
                        AutoCreate.CreateOrUpdate);
                }
                else
                {
                    opts.PersistMessagesWithSqlServer(Servers.SqlServerConnectionString, "conjoined_wolverine");
                    opts.Services.AddDbContextWithWolverineManagedConjoinedTenancy<ConjoinedItemsDbContext>(
                        (builder, connectionString) => builder.UseSqlServer(connectionString.Value),
                        AutoCreate.CreateOrUpdate);
                }

                opts.UseEntityFrameworkCoreTransactions();
                opts.UseEntityFrameworkCoreWolverineManagedMigrations();
                opts.Policies.AutoApplyTransactions();

                opts.Services.AddResourceSetupOnStartup();

                opts.PublishAllMessages().Locally();
            }).StartAsync();

        theBuilder = theHost.Services.GetRequiredService<IDbContextBuilder<ConjoinedItemsDbContext>>();

        // Start from clean tables every run
        var context = await theBuilder.BuildAsync(CancellationToken.None);
        await context.Items.IgnoreQueryFilters().ExecuteDeleteAsync();
        await context.Counters.IgnoreQueryFilters().ExecuteDeleteAsync();
        await context.GlobalThings.ExecuteDeleteAsync();
    }

    public async Task DisposeAsync()
    {
        await theHost.StopAsync();
        theHost.Dispose();
    }

    [Fact]
    public async Task tenanted_entities_are_mapped_with_tenant_id_column_filter_and_index()
    {
        var context = await theBuilder.BuildAsync(CancellationToken.None);

        var entityType = context.Model.FindEntityType(typeof(ConjoinedItem))!;
        var property = entityType.FindProperty(nameof(ConjoinedItem.TenantId))!;

        property.GetColumnName().ShouldBe(StorageConstants.TenantIdColumn);
        property.GetDefaultValue().ShouldBe(StorageConstants.DefaultTenantId);
        entityType.GetIndexes().ShouldContain(x => x.Properties.Any(p => p.Name == nameof(ConjoinedItem.TenantId)));
        entityType.GetQueryFilter().ShouldNotBeNull();

        // And an unmarked entity is left completely alone
        var globalType = context.Model.FindEntityType(typeof(GlobalThing))!;
        globalType.FindProperty(nameof(ConjoinedItem.TenantId)).ShouldBeNull();
        globalType.GetQueryFilter().ShouldBeNull();
    }

    [Fact]
    public async Task handler_insert_stamps_the_ambient_tenant_id()
    {
        var id = Guid.NewGuid();
        await theHost.ExecuteAndWaitAsync(c => c.InvokeForTenantAsync("green", new CreateConjoinedItem(id, "one")));

        var context = await theBuilder.BuildAsync("green", CancellationToken.None);
        var item = await context.Items.FindAsync(id);

        item.ShouldNotBeNull();
        item.TenantId.ShouldBe("green");
    }

    [Fact]
    public async Task insert_without_a_tenant_gets_the_default_tenant_sentinel()
    {
        var id = Guid.NewGuid();
        await theHost.InvokeMessageAndWaitAsync(new CreateConjoinedItem(id, "plain"));

        var context = await theBuilder.BuildAsync(CancellationToken.None);
        var item = await context.Items.FindAsync(id);

        item.ShouldNotBeNull();
        item.TenantId.ShouldBe(StorageConstants.DefaultTenantId);
    }

    [Fact]
    public async Task queries_are_bound_to_the_tenant_of_each_context_instance()
    {
        var greenId = Guid.NewGuid();
        var blueId = Guid.NewGuid();
        await theHost.ExecuteAndWaitAsync(c => c.InvokeForTenantAsync("green", new CreateConjoinedItem(greenId, "same")));
        await theHost.ExecuteAndWaitAsync(c => c.InvokeForTenantAsync("blue", new CreateConjoinedItem(blueId, "same")));

        // Alternating tenants on separate context instances proves the filter follows
        // the executing context instance, not a value captured when the model was cached
        var green = await theBuilder.BuildAsync("green", CancellationToken.None);
        var blue = await theBuilder.BuildAsync("blue", CancellationToken.None);
        var greenAgain = await theBuilder.BuildAsync("green", CancellationToken.None);

        (await green.Items.Where(x => x.Name == "same").ToListAsync()).Single().Id.ShouldBe(greenId);
        (await blue.Items.Where(x => x.Name == "same").ToListAsync()).Single().Id.ShouldBe(blueId);
        (await greenAgain.Items.Where(x => x.Name == "same").ToListAsync()).Single().Id.ShouldBe(greenId);
    }

    [Fact]
    public async Task find_async_respects_the_tenant_filter()
    {
        // Saga loads are generated as FindAsync, so tenant-scoped saga correctness
        // depends on this behavior
        var greenId = Guid.NewGuid();
        await theHost.ExecuteAndWaitAsync(c => c.InvokeForTenantAsync("green", new CreateConjoinedItem(greenId, "mine")));

        var blue = await theBuilder.BuildAsync("blue", CancellationToken.None);
        (await blue.Items.FindAsync(greenId)).ShouldBeNull();

        var green = await theBuilder.BuildAsync("green", CancellationToken.None);
        (await green.Items.FindAsync(greenId)).ShouldNotBeNull();
    }

    [Fact]
    public async Task cross_tenant_update_is_rejected()
    {
        var id = Guid.NewGuid();
        await theHost.ExecuteAndWaitAsync(c => c.InvokeForTenantAsync("green", new CreateConjoinedItem(id, "guarded")));

        var blue = await theBuilder.BuildAsync("blue", CancellationToken.None);
        var smuggled = await blue.Items.IgnoreQueryFilters().SingleAsync(x => x.Id == id);
        smuggled.Name = "hijacked";

        var ex = await Should.ThrowAsync<CrossTenantWriteException>(() => blue.SaveChangesAsync());
        ex.EntityTenantId.ShouldBe("green");
        ex.ContextTenantId.ShouldBe("blue");
    }

    [Fact]
    public async Task cross_tenant_delete_is_rejected()
    {
        var id = Guid.NewGuid();
        await theHost.ExecuteAndWaitAsync(c => c.InvokeForTenantAsync("green", new CreateConjoinedItem(id, "keeper")));

        var blue = await theBuilder.BuildAsync("blue", CancellationToken.None);
        var smuggled = await blue.Items.IgnoreQueryFilters().SingleAsync(x => x.Id == id);
        blue.Items.Remove(smuggled);

        await Should.ThrowAsync<CrossTenantWriteException>(() => blue.SaveChangesAsync());
    }

    [Fact]
    public async Task explicitly_stamped_foreign_tenant_id_on_insert_is_rejected()
    {
        var green = await theBuilder.BuildAsync("green", CancellationToken.None);
        green.Items.Add(new ConjoinedItem { Id = Guid.NewGuid(), Name = "smuggle", TenantId = "blue" });

        await Should.ThrowAsync<CrossTenantWriteException>(() => green.SaveChangesAsync());
    }

    [Fact]
    public async Task sagas_are_tenant_scoped()
    {
        var id = Guid.NewGuid();
        await theHost.ExecuteAndWaitAsync(c => c.InvokeForTenantAsync("green", new StartCounter(id)));
        await theHost.ExecuteAndWaitAsync(c => c.InvokeForTenantAsync("green", new IncrementCounter(id)));

        // The same saga id from another tenant must not load green's saga
        await Should.ThrowAsync<UnknownSagaException>(() => theHost.TrackActivity()
            .DoNotAssertOnExceptionsDetected()
            .ExecuteAndWaitAsync(c => c.InvokeForTenantAsync("blue", new IncrementCounter(id))));

        var green = await theBuilder.BuildAsync("green", CancellationToken.None);
        var saga = await green.Counters.SingleAsync(x => x.Id == id);
        saga.Count.ShouldBe(1);
        saga.TenantId.ShouldBe("green");

        var blue = await theBuilder.BuildAsync("blue", CancellationToken.None);
        (await blue.Counters.FindAsync(id)).ShouldBeNull();
    }
}

public class conjoined_tenancy_with_postgresql : ConjoinedTenancyCompliance
{
    public conjoined_tenancy_with_postgresql() : base(DatabaseEngine.PostgreSQL)
    {
    }
}

public class conjoined_tenancy_with_sqlserver : ConjoinedTenancyCompliance
{
    public conjoined_tenancy_with_sqlserver() : base(DatabaseEngine.SqlServer)
    {
    }
}
