using IntegrationTests;
using JasperFx;
using JasperFx.MultiTenancy;
using JasperFx.Resources;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine;
using Wolverine.EntityFrameworkCore;
using Wolverine.EntityFrameworkCore.Internals;
using Wolverine.Postgresql;
using Wolverine.Tracking;

namespace EfCoreTests.MultiTenancy.ConjoinedTenancy;

/// <summary>
///     GH-4629 and the set-based half of GH-4632. <c>TenantStampingInterceptor</c> only hooks
///     <c>SavingChanges</c>, so a raw <c>ExecuteUpdate</c> or <c>ExecuteDelete</c> walks past every
///     conjoined guard except the query filter: it can move a row to another tenant with
///     <c>SetProperty(x =&gt; x.TenantId, ...)</c>, it can cross tenants outright with
///     <c>IgnoreQueryFilters()</c>, and it never asks whether the tenant is switched off. The
///     <see cref="EfCoreOp" /> family carries those rules itself.
/// </summary>
[Collection("multi-tenancy")]
public class conjoined_efcore_ops : IAsyncLifetime
{
    private IDbContextBuilder<ScopedItemsDbContext> theBuilder = null!;
    private IHost theHost = null!;

    public async ValueTask InitializeAsync()
    {
        theHost = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Durability.Mode = DurabilityMode.Solo;

                opts.Discovery.DisableConventionalDiscovery()
                    .IncludeType<ScopedItemHandler>();

                opts.PersistMessagesWithPostgresql(Servers.PostgresConnectionString, "conjoined_ops");
                opts.Services.AddDbContextWithWolverineManagedConjoinedTenancy<ScopedItemsDbContext>(
                    (builder, connectionString) => builder.UseNpgsql(connectionString.Value),
                    AutoCreate.CreateOrUpdate);

                opts.UseEntityFrameworkCoreTransactions();
                opts.UseEntityFrameworkCoreWolverineManagedMigrations();
                opts.Policies.AutoApplyTransactions();

                opts.Services.AddResourceSetupOnStartup();

                opts.PublishAllMessages().Locally();
            }).StartAsync();

        theBuilder = theHost.Services.GetRequiredService<IDbContextBuilder<ScopedItemsDbContext>>();

        var context = await theBuilder.BuildAsync(CancellationToken.None);
        await context.Items.IgnoreQueryFilters().ExecuteDeleteAsync(CancellationToken.None);
    }

    public async ValueTask DisposeAsync()
    {
        await theHost.StopAsync(CancellationToken.None);
        theHost.Dispose();
    }

    private async Task<Guid> createAsync(string tenantId, string name)
    {
        var id = Guid.NewGuid();
        await theHost.ExecuteAndWaitAsync(c => c.InvokeForTenantAsync(tenantId, new CreateScopedItem(id, name)));
        return id;
    }

    private async Task<ScopedItem> readAsync(string tenantId, Guid id)
    {
        var context = await theBuilder.BuildAsync(tenantId, CancellationToken.None);
        return await context.Items.AsNoTracking().SingleAsync(x => x.Id == id, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task execute_update_op_is_scoped_to_the_tenant()
    {
        var greenId = await createAsync("green", "green's own");

        // Blue asks for the same id. The op appends blue's tenant predicate, so the statement matches
        // nothing at all rather than reaching into green.
        await theHost.ExecuteAndWaitAsync(c => c.InvokeForTenantAsync("blue", new ArchiveScopedItem(greenId)));

        (await readAsync("green", greenId)).Archived.ShouldBeFalse();

        // ...and green's own call does what it says
        await theHost.ExecuteAndWaitAsync(c => c.InvokeForTenantAsync("green", new ArchiveScopedItem(greenId)));

        (await readAsync("green", greenId)).Archived.ShouldBeTrue();
    }

    [Fact]
    public async Task execute_update_op_refuses_to_set_tenant_id()
    {
        var greenId = await createAsync("green", "staying put");

        await Should.ThrowAsync<CrossTenantWriteException>(() => theHost.TrackActivity()
            .DoNotAssertOnExceptionsDetected()
            .ExecuteAndWaitAsync(c =>
                c.InvokeForTenantAsync("green", new MoveScopedItemToTenant(greenId, "blue"))));

        var survivor = await readAsync("green", greenId);
        survivor.TenantId.ShouldBe("green");
    }

    [Fact]
    public async Task ignore_query_filters_execute_delete_is_refused()
    {
        var greenId = await createAsync("green", "not yours");

        await Should.ThrowAsync<CrossTenantWriteException>(() => theHost.TrackActivity()
            .DoNotAssertOnExceptionsDetected()
            .ExecuteAndWaitAsync(c =>
                c.InvokeForTenantAsync("blue", new PurgeScopedItemIgnoringFilters(greenId))));

        (await readAsync("green", greenId)).ShouldNotBeNull();
    }

    [Fact]
    public async Task execute_update_op_against_a_disabled_tenant_is_refused()
    {
        var tenants = theHost.Services.GetRequiredService<IDynamicTenantSource<string>>();
        var tenant = "ops_" + Guid.NewGuid().ToString("N")[..8];

        await tenants.AddTenantAsync(tenant, CancellationToken.None);
        var id = await createAsync(tenant, "before the lights went out");

        await tenants.DisableTenantAsync(tenant);

        // The op is run directly against a context already pinned to the disabled tenant, which is the
        // only way past the refusal that tenant RESOLUTION already makes -- and exactly the gap that
        // TenantStampingInterceptor closes for SaveChanges and could not close for a set-based write.
        var context = await theBuilder.BuildAsync(tenant, CancellationToken.None);
        var op = EfCoreOps.ExecuteUpdate<ScopedItem>(x => x.Id == id,
            setters => setters.SetProperty(x => x.Archived, true));

        await Should.ThrowAsync<DisabledTenantException>(() =>
            op.ExecuteAsync(context, TestContext.Current.CancellationToken));

        await tenants.EnableTenantAsync(tenant);
        (await readAsync(tenant, id)).Archived.ShouldBeFalse();
    }

    /// <summary>
    ///     Characterizes what GH-4632 leaves open. The declarative operations carry the conjoined
    ///     rules; a raw <c>ExecuteUpdate</c> through the <c>DbContext</c> still does not, because
    ///     <c>TenantStampingInterceptor</c> is a <c>SavingChanges</c> interceptor and there is no
    ///     <c>SaveChanges</c> in a set-based write. Rewrite this test the day an
    ///     <c>IQueryExpressionInterceptor</c> closes that door too.
    /// </summary>
    [Fact]
    public async Task raw_execute_update_still_walks_past_the_conjoined_guards()
    {
        var greenId = await createAsync("green", "raw");

        var blue = await theBuilder.BuildAsync("blue", CancellationToken.None);

        var moved = await blue.Items.IgnoreQueryFilters().Where(x => x.Id == greenId)
            .ExecuteUpdateAsync(setters => setters.SetProperty(x => x.TenantId, "blue"),
                TestContext.Current.CancellationToken);

        moved.ShouldBe(1);
        (await readAsync("blue", greenId)).TenantId.ShouldBe("blue");
    }
}
