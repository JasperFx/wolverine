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

    public async ValueTask InitializeAsync()
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

    public async ValueTask DisposeAsync()
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
        // EF 10 replaced the single anonymous filter with NAMED filters, and the customizer
        // registers under ConjoinedTenancy.QueryFilterName there -- so GetQueryFilter(), the EF 8/9
        // accessor, reads null on EF 10 even though the filter is applied. Asserting through the
        // old accessor on net10 reports a broken feature that works. GH-3540.
#if NET10_0_OR_GREATER
        entityType.GetDeclaredQueryFilters()
            .ShouldContain(x => x.Key == Wolverine.EntityFrameworkCore.Internals.ConjoinedTenancy.QueryFilterName);
#else
        entityType.GetQueryFilter().ShouldNotBeNull();
#endif

        // And an unmarked entity is left completely alone
        var globalType = context.Model.FindEntityType(typeof(GlobalThing))!;
        globalType.FindProperty(nameof(ConjoinedItem.TenantId)).ShouldBeNull();
#if NET10_0_OR_GREATER
        globalType.GetDeclaredQueryFilters().ShouldBeEmpty();
#else
        globalType.GetQueryFilter().ShouldBeNull();
#endif
    }

    [Fact]
    public async Task handler_insert_stamps_the_ambient_tenant_id()
    {
        var id = Guid.NewGuid();
        await theHost.ExecuteAndWaitAsync(c => c.InvokeForTenantAsync("green", new CreateConjoinedItem(id, "one")));

        var context = await theBuilder.BuildAsync("green", CancellationToken.None);
        var item = await context.Items.FindAsync(new object?[] { id }, TestContext.Current.CancellationToken);

        item.ShouldNotBeNull();
        item.TenantId.ShouldBe("green");
    }

    [Fact]
    public async Task insert_without_a_tenant_gets_the_default_tenant_sentinel()
    {
        var id = Guid.NewGuid();
        await theHost.InvokeMessageAndWaitAsync(new CreateConjoinedItem(id, "plain"));

        var context = await theBuilder.BuildAsync(CancellationToken.None);
        var item = await context.Items.FindAsync(new object?[] { id }, TestContext.Current.CancellationToken);

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

        (await green.Items.Where(x => x.Name == "same").ToListAsync(cancellationToken: TestContext.Current.CancellationToken)).Single().Id.ShouldBe(greenId);
        (await blue.Items.Where(x => x.Name == "same").ToListAsync(cancellationToken: TestContext.Current.CancellationToken)).Single().Id.ShouldBe(blueId);
        (await greenAgain.Items.Where(x => x.Name == "same").ToListAsync(cancellationToken: TestContext.Current.CancellationToken)).Single().Id.ShouldBe(greenId);
    }

    [Fact]
    public async Task find_async_respects_the_tenant_filter()
    {
        // Saga loads are generated as FindAsync, so tenant-scoped saga correctness
        // depends on this behavior
        var greenId = Guid.NewGuid();
        await theHost.ExecuteAndWaitAsync(c => c.InvokeForTenantAsync("green", new CreateConjoinedItem(greenId, "mine")));

        var blue = await theBuilder.BuildAsync("blue", CancellationToken.None);
        (await blue.Items.FindAsync(new object?[] { greenId }, TestContext.Current.CancellationToken)).ShouldBeNull();

        var green = await theBuilder.BuildAsync("green", CancellationToken.None);
        (await green.Items.FindAsync(new object?[] { greenId }, TestContext.Current.CancellationToken)).ShouldNotBeNull();
    }

    [Fact]
    public async Task cross_tenant_update_is_rejected()
    {
        var id = Guid.NewGuid();
        await theHost.ExecuteAndWaitAsync(c => c.InvokeForTenantAsync("green", new CreateConjoinedItem(id, "guarded")));

        var blue = await theBuilder.BuildAsync("blue", CancellationToken.None);
        var smuggled = await blue.Items.IgnoreQueryFilters().SingleAsync(x => x.Id == id, cancellationToken: TestContext.Current.CancellationToken);
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
        var smuggled = await blue.Items.IgnoreQueryFilters().SingleAsync(x => x.Id == id, cancellationToken: TestContext.Current.CancellationToken);
        blue.Items.Remove(smuggled);

        await Should.ThrowAsync<CrossTenantWriteException>(() => blue.SaveChangesAsync());
    }

    /// <summary>
    ///     GH-4612. The two tests above only cover an entity read out of the database, whose
    ///     <c>TenantId</c> already carries the other tenant's id for the interceptor to catch. A
    ///     DETACHED entity -- <c>Update(new ConjoinedItem { Id = someoneElsesId })</c>, the shape an HTTP
    ///     request body or a message payload produces -- has a null <c>TenantId</c> only because it was
    ///     never loaded, so the interceptor stamped it with the caller's tenant, the cross-tenant check
    ///     passed, and EF matched the row by key alone. The write not only crossed the boundary, it moved
    ///     the row into the caller's tenant.
    /// </summary>
    [Fact]
    public async Task detached_update_cannot_reach_another_tenants_row()
    {
        var id = Guid.NewGuid();
        await theHost.ExecuteAndWaitAsync(c => c.InvokeForTenantAsync("green", new CreateConjoinedItem(id, "green's own")));

        var blue = await theBuilder.BuildAsync("blue", CancellationToken.None);
        blue.Items.Update(new ConjoinedItem { Id = id, Name = "hijacked by blue" });

        // Either refusal is acceptable: a CrossTenantWriteException, or EF's own "affected 0 rows"
        // report once the write is scoped to the caller's tenant in SQL. What is NOT acceptable is a
        // silent success.
        await Should.ThrowAsync<Exception>(() => blue.SaveChangesAsync());

        var green = await theBuilder.BuildAsync("green", CancellationToken.None);
        var survivor = await green.Items.SingleAsync(x => x.Id == id, TestContext.Current.CancellationToken);
        survivor.Name.ShouldBe("green's own");
        survivor.TenantId.ShouldBe("green");
    }

    /// <summary>
    ///     GH-4612, the delete half. EF emitted <c>DELETE ... WHERE Id = @p0</c> with no tenant predicate
    ///     at all, so another tenant's row simply disappeared.
    /// </summary>
    [Fact]
    public async Task detached_delete_cannot_reach_another_tenants_row()
    {
        var id = Guid.NewGuid();
        await theHost.ExecuteAndWaitAsync(c => c.InvokeForTenantAsync("green", new CreateConjoinedItem(id, "green's own")));

        var blue = await theBuilder.BuildAsync("blue", CancellationToken.None);
        blue.Items.Remove(new ConjoinedItem { Id = id });

        await Should.ThrowAsync<Exception>(() => blue.SaveChangesAsync());

        var green = await theBuilder.BuildAsync("green", CancellationToken.None);
        (await green.Items.FindAsync([id], TestContext.Current.CancellationToken)).ShouldNotBeNull();
    }

    /// <summary>
    ///     GH-4612 positive control. Scoping detached writes to the context's tenant must not break the
    ///     legitimate case: a detached update or delete of the caller's OWN row still has to work, since
    ///     that is exactly what a <c>Storage.Update()</c> / <c>Storage.Delete()</c> return value of an
    ///     entity built from the message does (GH-4613).
    /// </summary>
    [Fact]
    public async Task detached_update_and_delete_of_your_own_row_still_work()
    {
        var updateId = Guid.NewGuid();
        var deleteId = Guid.NewGuid();
        await theHost.ExecuteAndWaitAsync(c => c.InvokeForTenantAsync("green", new CreateConjoinedItem(updateId, "before")));
        await theHost.ExecuteAndWaitAsync(c => c.InvokeForTenantAsync("green", new CreateConjoinedItem(deleteId, "doomed")));

        var writer = await theBuilder.BuildAsync("green", CancellationToken.None);
        writer.Items.Update(new ConjoinedItem { Id = updateId, Name = "after" });
        await writer.SaveChangesAsync(TestContext.Current.CancellationToken);

        var remover = await theBuilder.BuildAsync("green", CancellationToken.None);
        remover.Items.Remove(new ConjoinedItem { Id = deleteId });
        await remover.SaveChangesAsync(TestContext.Current.CancellationToken);

        var reader = await theBuilder.BuildAsync("green", CancellationToken.None);
        var updated = await reader.Items.SingleAsync(x => x.Id == updateId, TestContext.Current.CancellationToken);
        updated.Name.ShouldBe("after");
        updated.TenantId.ShouldBe("green");
        (await reader.Items.FindAsync([deleteId], TestContext.Current.CancellationToken)).ShouldBeNull();
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
        var saga = await green.Counters.SingleAsync(x => x.Id == id, cancellationToken: TestContext.Current.CancellationToken);
        saga.Count.ShouldBe(1);
        saga.TenantId.ShouldBe("green");

        var blue = await theBuilder.BuildAsync("blue", CancellationToken.None);
        (await blue.Counters.FindAsync(new object?[] { id }, TestContext.Current.CancellationToken)).ShouldBeNull();
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
