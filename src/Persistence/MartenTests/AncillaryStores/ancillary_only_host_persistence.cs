using IntegrationTests;
using JasperFx.Resources;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine;
using Wolverine.Marten;
using Wolverine.Persistence;
using Wolverine.Persistence.Sagas;
using Wolverine.Runtime;
using Wolverine.Tracking;

namespace MartenTests.AncillaryStores;

// GH-4456: a host that has ONLY ancillary Marten stores -- AddMartenStore<T>().IntegrateWithWolverine()
// with no AddMarten(...) at all, which is what a modular monolith giving every bounded context its own
// store ends up with -- never got the Marten codegen wiring. MartenIntegration (and with it
// InsertFirstPersistenceStrategy<MartenPersistenceFrameProvider>) was registered only by the MAIN store's
// IntegrateWithWolverine(), so [Entity] fell through to the catch-all InMemoryPersistenceFrameProvider and
// read the document out of an in-memory dictionary that nothing ever populates. The entity was always null,
// the not-null guard stopped the chain, and the handler never ran -- no exception, nothing logged.
//
// These tests pin the whole declarative-persistence surface against an ancillary-only host, not just
// [Entity]: the reads ([Entity], [All], [FirstOrDefault], [Queryable]) and the writes (IStorageAction<T>,
// IMartenOp, an injected IDocumentSession), because they all resolve through the same missing provider.
public class ancillary_only_host_persistence : IAsyncLifetime
{
    private IHost theHost = null!;

    public async ValueTask InitializeAsync()
    {
        theHost = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Durability.Mode = DurabilityMode.Solo;
                opts.Policies.AutoApplyTransactions();

                // Deliberately NO AddMarten(...). This host's only Marten store is the ancillary one.
                opts.Services.AddMartenStore<IAncillaryOnlyStore>(m =>
                    {
                        m.Connection(Servers.PostgresConnectionString);
                        m.DatabaseSchemaName = "ancillary_only";
                        m.Events.DatabaseSchemaName = "ancillary_only";
                    })
                    .IntegrateWithWolverine();

                opts.Discovery.DisableConventionalDiscovery()
                    .IncludeType(typeof(AncillaryOnlyHandler));

                opts.Services.AddResourceSetupOnStartup();
            }).StartAsync();

        var store = theHost.DocumentStore<IAncillaryOnlyStore>();
        await store.Advanced.Clean.DeleteDocumentsByTypeAsync(typeof(AncillaryOnlyDoc));
    }

    public async ValueTask DisposeAsync()
    {
        await theHost.StopAsync();
        theHost.Dispose();
    }

    private IDocumentStore theStore => theHost.DocumentStore<IAncillaryOnlyStore>();

    [Fact]
    public void the_marten_persistence_strategy_is_registered()
    {
        // The root cause, asserted directly. Without the Marten strategy in the list the generated code
        // silently uses InMemoryPersistenceFrameProvider's saga persistor, and every test below is a
        // confusing runtime failure instead of a legible one.
        var runtime = theHost.Services.GetRequiredService<IWolverineRuntime>();
        runtime.Options.CodeGeneration.PersistenceProviders()
            .ShouldContain(x => x is Wolverine.Marten.Persistence.Sagas.MartenPersistenceFrameProvider);
    }

    [Fact]
    public async Task entity_attribute_loads_from_the_ancillary_store()
    {
        var id = Guid.NewGuid();
        await using (var session = theStore.LightweightSession())
        {
            session.Store(new AncillaryOnlyDoc { Id = id, Name = "original" });
            await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await theHost.InvokeMessageAndWaitAsync(new RenameAncillaryOnlyDoc(id, "renamed"));

        await using var query = theStore.QuerySession();
        var doc = await query.LoadAsync<AncillaryOnlyDoc>(id, TestContext.Current.CancellationToken);
        doc.ShouldNotBeNull();

        // The heart of GH-4456. With the in-memory provider the [Entity] parameter was null, the guard
        // stopped the chain, and this document was never touched -- while the test host started cleanly
        // and nothing was logged.
        doc.Name.ShouldBe("renamed");
    }

    [Fact]
    public async Task storage_action_writes_through_the_ancillary_store()
    {
        var id = Guid.NewGuid();
        await theHost.InvokeMessageAndWaitAsync(new InsertAncillaryOnlyDoc(id, "inserted"));

        await using var query = theStore.QuerySession();
        var doc = await query.LoadAsync<AncillaryOnlyDoc>(id, TestContext.Current.CancellationToken);
        doc.ShouldNotBeNull();
        doc.Name.ShouldBe("inserted");
    }

    [Fact]
    public async Task marten_op_writes_through_the_ancillary_store()
    {
        var id = Guid.NewGuid();
        await theHost.InvokeMessageAndWaitAsync(new StoreAncillaryOnlyDoc(id, "stored"));

        await using var query = theStore.QuerySession();
        var doc = await query.LoadAsync<AncillaryOnlyDoc>(id, TestContext.Current.CancellationToken);
        doc.ShouldNotBeNull();
        doc.Name.ShouldBe("stored");
    }

    [Fact]
    public async Task injected_session_commits_against_the_ancillary_store()
    {
        var id = Guid.NewGuid();
        await theHost.InvokeMessageAndWaitAsync(new SessionAncillaryOnlyDoc(id, "session"));

        await using var query = theStore.QuerySession();
        var doc = await query.LoadAsync<AncillaryOnlyDoc>(id, TestContext.Current.CancellationToken);
        doc.ShouldNotBeNull();
        doc.Name.ShouldBe("session");
    }

    [Fact]
    public async Task all_and_first_or_default_and_queryable_read_the_ancillary_store()
    {
        // [Entity]'s siblings go through the same provider, so they broke in the same silent way.
        var id = Guid.NewGuid();
        await using (var session = theStore.LightweightSession())
        {
            session.Store(new AncillaryOnlyDoc { Id = id, Name = "readable" });
            await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await theHost.InvokeMessageAndWaitAsync(new CountAncillaryOnlyDocs());

        var counts = AncillaryOnlyHandler.LastCounts.ShouldNotBeNull();
        counts.All.ShouldBeGreaterThan(0);
        counts.FirstOrDefault.ShouldNotBeNull();
        counts.Queryable.ShouldBeGreaterThan(0);
    }
}

public interface IAncillaryOnlyStore : IDocumentStore;

public class AncillaryOnlyDoc
{
    public Guid Id { get; set; }
    public string Name { get; set; } = null!;
}

public record RenameAncillaryOnlyDoc(Guid Id, string Name);

public record InsertAncillaryOnlyDoc(Guid Id, string Name);

public record StoreAncillaryOnlyDoc(Guid Id, string Name);

public record SessionAncillaryOnlyDoc(Guid Id, string Name);

public record CountAncillaryOnlyDocs;

public record AncillaryOnlyCounts(int All, AncillaryOnlyDoc? FirstOrDefault, int Queryable);

[MartenStore(typeof(IAncillaryOnlyStore))]
public static class AncillaryOnlyHandler
{
    public static AncillaryOnlyCounts? LastCounts { get; private set; }

    public static void Handle(RenameAncillaryOnlyDoc command, [Entity] AncillaryOnlyDoc doc,
        IDocumentSession session)
    {
        doc.Name = command.Name;
        session.Store(doc);
    }

    public static IStorageAction<AncillaryOnlyDoc> Handle(InsertAncillaryOnlyDoc command)
    {
        return Storage.Insert(new AncillaryOnlyDoc { Id = command.Id, Name = command.Name });
    }

    public static IMartenOp Handle(StoreAncillaryOnlyDoc command)
    {
        return MartenOps.Store(new AncillaryOnlyDoc { Id = command.Id, Name = command.Name });
    }

    public static void Handle(SessionAncillaryOnlyDoc command, IDocumentSession session)
    {
        session.Store(new AncillaryOnlyDoc { Id = command.Id, Name = command.Name });
    }

    public static async Task Handle(CountAncillaryOnlyDocs _,
        [All] IReadOnlyList<AncillaryOnlyDoc> all,
        [FirstOrDefault] AncillaryOnlyDoc? first,
        [Queryable] IQueryable<AncillaryOnlyDoc> queryable,
        CancellationToken token)
    {
        LastCounts = new AncillaryOnlyCounts(all.Count, first, await queryable.CountAsync(token));
    }
}
