using IntegrationTests;
using JasperFx;
using JasperFx.Resources;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine;
using Wolverine.EntityFrameworkCore;
using Wolverine.EntityFrameworkCore.Internals;
using Wolverine.Persistence;
using Wolverine.Postgresql;
using Wolverine.Tracking;

namespace EfCoreTests.MultiTenancy.ConjoinedTenancy;

/// <summary>
///     GH-4611. <see cref="TransactionMiddlewareMode.Lightweight" /> never took the multi-tenanted
///     branch of <c>EFCorePersistenceFrameProvider.ApplyTransactionSupport</c>, so a conjoined
///     <c>DbContext</c> was resolved straight out of the container -- where the only registration is the
///     one marked "STRICTLY FOR EF CORE MIGRATIONS", built through <c>BuildForMain()</c> and therefore
///     pinned to <c>*DEFAULT*</c>. Writes were stamped with the default tenant sentinel and every tenant
///     could read them. <c>Eager</c> mode handles this correctly, and the conjoined compliance battery
///     only ever runs in <c>Eager</c>.
/// </summary>
[Collection("multi-tenancy")]
public class Bug_4611_lightweight_conjoined_tenancy : IAsyncLifetime
{
    private IDbContextBuilder<ConjoinedItemsDbContext> theBuilder = null!;
    private IHost theHost = null!;

    public async ValueTask InitializeAsync()
    {
        theHost = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Durability.Mode = DurabilityMode.Solo;

                opts.Discovery.DisableConventionalDiscovery()
                    .IncludeType<ConjoinedItemHandler>();

                opts.PersistMessagesWithPostgresql(Servers.PostgresConnectionString, "conjoined_lw_wolverine");
                opts.Services.AddDbContextWithWolverineManagedConjoinedTenancy<ConjoinedItemsDbContext>(
                    (builder, connectionString) => builder.UseNpgsql(connectionString.Value),
                    AutoCreate.CreateOrUpdate);

                // The whole point of this battery: everything below is identical to
                // ConjoinedTenancyCompliance except the transaction mode
                opts.UseEntityFrameworkCoreTransactions(TransactionMiddlewareMode.Lightweight);
                opts.UseEntityFrameworkCoreWolverineManagedMigrations();
                opts.Policies.AutoApplyTransactions();

                opts.Services.AddResourceSetupOnStartup();

                opts.PublishAllMessages().Locally();
            }).StartAsync();

        theBuilder = theHost.Services.GetRequiredService<IDbContextBuilder<ConjoinedItemsDbContext>>();

        var context = await theBuilder.BuildAsync(CancellationToken.None);
        await context.Items.IgnoreQueryFilters().ExecuteDeleteAsync(TestContext.Current.CancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        await theHost.StopAsync();
        theHost.Dispose();
    }

    [Fact]
    public async Task handler_insert_stamps_the_ambient_tenant_id_in_lightweight_mode()
    {
        var id = Guid.NewGuid();
        await theHost.ExecuteAndWaitAsync(c =>
            c.InvokeForTenantAsync("green", new CreateConjoinedItem(id, "one")));

        var green = await theBuilder.BuildAsync("green", CancellationToken.None);
        var item = await green.Items.FindAsync([id], TestContext.Current.CancellationToken);

        item.ShouldNotBeNull();
        item.TenantId.ShouldBe("green");
    }

    /// <summary>
    ///     Deliberately asserted as "each tenant sees exactly its own row" rather than "blue cannot see
    ///     green's row". The latter passes vacuously against the bug: both writes landed under the
    ///     <c>*DEFAULT*</c> sentinel, which blue cannot see either.
    /// </summary>
    [Fact]
    public async Task writes_in_lightweight_mode_are_scoped_to_their_own_tenant()
    {
        var greenId = Guid.NewGuid();
        var blueId = Guid.NewGuid();
        await theHost.ExecuteAndWaitAsync(c =>
            c.InvokeForTenantAsync("green", new CreateConjoinedItem(greenId, "same")));
        await theHost.ExecuteAndWaitAsync(c =>
            c.InvokeForTenantAsync("blue", new CreateConjoinedItem(blueId, "same")));

        var green = await theBuilder.BuildAsync("green", CancellationToken.None);
        var blue = await theBuilder.BuildAsync("blue", CancellationToken.None);

        (await green.Items.Where(x => x.Name == "same").ToListAsync(TestContext.Current.CancellationToken))
            .Single().Id.ShouldBe(greenId);
        (await blue.Items.Where(x => x.Name == "same").ToListAsync(TestContext.Current.CancellationToken))
            .Single().Id.ShouldBe(blueId);
    }
}
