using IntegrationTests;
using JasperFx.Resources;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine;
using Wolverine.Marten;
using Wolverine.Persistence.Durability;
using Wolverine.Tracking;
using Wolverine.Util;

namespace MartenTests.AncillaryStores;

// Regression test for GH-3953. Reported by @kentcooper.
//
// A handler whose transaction is owned by the MAIN Marten store's IDocumentSession also depends -- for
// read-only reference lookups -- on an ancillary Marten store, either directly or transitively through
// another service. The recursive ServiceDependencies() inference added for GH-3870
// (WolverineRuntime.HostService.inferAncillaryStoreType) saw the single ancillary marker anywhere in that
// dependency graph and routed the handler's inbox / dead letter row into the ancillary store, even though
// nothing in the handler ever commits there.
//
// That inference is only correct for a provider whose transaction owner IS a plain service dependency --
// EF Core, where DetermineDbContextType picks the injected DbContext. For Marten (and Polecat / Fisher)
// the ancillary store has to be named with [MartenStore] / [Storage], which populates
// chain.AncillaryStoreType directly; merely injecting the store interface says nothing about who owns the
// transaction.
//
// These assertions read the routing DECISION rather than pushing messages through a broker: it is exactly
// the value DurableReceiver and DurableLocalQueue consult per envelope, and it isolates the inference from
// everything downstream of it.
public class Bug_3953_read_only_ancillary_dependency : IAsyncLifetime
{
    private IHost theHost = null!;

    public async ValueTask InitializeAsync()
    {
        theHost = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Durability.Mode = DurabilityMode.Solo;
                opts.Policies.AutoApplyTransactions();

                opts.Services.AddMarten(m =>
                {
                    m.Connection(Servers.PostgresConnectionString);
                    m.DatabaseSchemaName = "bug3953_main";
                    m.Events.DatabaseSchemaName = "bug3953_main";
                }).IntegrateWithWolverine();

                opts.Services.AddMartenStore<IBug3953SystemStore>(m =>
                {
                    m.Connection(Servers.PostgresConnectionString);
                    m.DatabaseSchemaName = "bug3953_system";
                    m.Events.DatabaseSchemaName = "bug3953_system";
                }).IntegrateWithWolverine();

                opts.Services.AddScoped<IBug3953Directory, Bug3953Directory>();

                opts.Discovery.DisableConventionalDiscovery()
                    .IncludeType(typeof(Bug3953Handlers))
                    .IncludeType(typeof(Bug3953SystemStoreHandler));

                opts.Services.AddResourceSetupOnStartup();
            }).StartAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await theHost.StopAsync();
        theHost.Dispose();
    }

    private IMessageStore? ancillaryStoreFor<T>()
    {
        return theHost.GetRuntime().Stores
            .TryFindAncillaryStoreForMessageType(null, typeof(T).ToMessageTypeName());
    }

    [Fact]
    public void a_directly_injected_ancillary_store_does_not_claim_the_inbox()
    {
        ancillaryStoreFor<Bug3953DirectMessage>()
            .ShouldBeNull(
                "The handler's transaction is owned by the main store's IDocumentSession; the ancillary " +
                "store is only queried.");
    }

    [Fact]
    public void a_transitively_injected_ancillary_store_does_not_claim_the_inbox()
    {
        ancillaryStoreFor<Bug3953TransitiveMessage>()
            .ShouldBeNull(
                "Same as the direct case, but the ancillary store is reached through another service's " +
                "constructor.");
    }

    [Fact]
    public void an_explicitly_attributed_handler_still_claims_the_inbox()
    {
        // The control. Narrowing the GH-3870 inference must not weaken the attribute path that
        // GH-2576 / GH-2944 / GH-3109 depend on.
        var store = ancillaryStoreFor<Bug3953AttributedMessage>();
        store.ShouldNotBeNull();
        store.ShouldBeSameAs(theHost.GetRuntime().Stores.FindAncillaryStore(typeof(IBug3953SystemStore)));
    }
}

public interface IBug3953SystemStore : IDocumentStore;

public record Bug3953DirectMessage(Guid Id);

public record Bug3953TransitiveMessage(Guid Id);

public record Bug3953AttributedMessage(Guid Id);

public class Bug3953Doc
{
    public Guid Id { get; set; }
}

public class Bug3953Reference
{
    public Guid Id { get; set; }
}

public interface IBug3953Directory
{
    Task<bool> ExistsAsync(Guid id, CancellationToken token);
}

// The ancillary store is QUERIED here and nothing more -- it never owns a transaction.
public class Bug3953Directory : IBug3953Directory
{
    private readonly IBug3953SystemStore _systemStore;

    public Bug3953Directory(IBug3953SystemStore systemStore) => _systemStore = systemStore;

    public async Task<bool> ExistsAsync(Guid id, CancellationToken token)
    {
        await using var query = _systemStore.QuerySession();
        return await query.Query<Bug3953Reference>().AnyAsync(x => x.Id == id, token);
    }
}

public static class Bug3953Handlers
{
    public static async Task Handle(Bug3953DirectMessage message, IDocumentSession session,
        IBug3953SystemStore systemStore, CancellationToken token)
    {
        await using var query = systemStore.QuerySession();
        await query.Query<Bug3953Reference>().AnyAsync(x => x.Id == message.Id, token);

        session.Store(new Bug3953Doc { Id = message.Id });
    }

    public static async Task Handle(Bug3953TransitiveMessage message, IDocumentSession session,
        IBug3953Directory directory, CancellationToken token)
    {
        await directory.ExistsAsync(message.Id, token);

        session.Store(new Bug3953Doc { Id = message.Id });
    }
}

[MartenStore(typeof(IBug3953SystemStore))]
public static class Bug3953SystemStoreHandler
{
    public static void Handle(Bug3953AttributedMessage message, IDocumentSession session)
    {
        session.Store(new Bug3953Reference { Id = message.Id });
    }
}
