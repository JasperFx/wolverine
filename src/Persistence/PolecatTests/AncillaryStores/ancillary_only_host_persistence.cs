using IntegrationTests;
using JasperFx.Resources;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Polecat;
using Shouldly;
using Wolverine;
using Wolverine.Persistence;
using Wolverine.Persistence.Sagas;
using Wolverine.Polecat;
using Wolverine.Runtime;
using Wolverine.Tracking;

namespace PolecatTests.AncillaryStores;

// GH-4462, the Polecat twin of GH-4456. A host whose only Polecat stores are ancillary --
// AddPolecatStore<T>().IntegrateWithWolverine() with no AddPolecat(...), which is where a modular
// monolith that gives each bounded context its own store and needs no main store ends up -- never
// registered PolecatIntegration, and that extension is what calls
// InsertFirstPersistenceStrategy<PolecatPersistenceFrameProvider>(). So [Entity] fell through to the
// catch-all InMemoryPersistenceFrameProvider and read the document out of an in-memory dictionary that
// nothing ever populates: the entity was always null, the not-null guard stopped the chain, and the
// handler never ran -- with no exception and nothing logged.
//
// These pin the whole declarative-persistence surface, not just [Entity], because it all resolves
// through the same missing provider.
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

                // Deliberately NO AddPolecat(...). This host's only Polecat store is the ancillary one.
                opts.Services.AddPolecatStore<IAncillaryOnlyStore>(m =>
                    {
                        m.Connection(Servers.SqlServerConnectionString);
                        m.DatabaseSchemaName = "ancillary_only";
                    })
                    .IntegrateWithWolverine();

                opts.Discovery.DisableConventionalDiscovery()
                    .IncludeType(typeof(AncillaryOnlyHandler));

                opts.Services.AddResourceSetupOnStartup();
            }).StartAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await theHost.StopAsync();
        theHost.Dispose();
    }

    private IDocumentStore theStore => theHost.Services.GetRequiredService<IAncillaryOnlyStore>();

    [Fact]
    public void the_polecat_persistence_strategy_is_registered()
    {
        // The root cause, asserted directly, so a regression reads as one legible failure rather than
        // five confusing ones.
        var runtime = theHost.Services.GetRequiredService<IWolverineRuntime>();
        runtime.Options.CodeGeneration.PersistenceProviders()
            .ShouldContain(x => x is Wolverine.Polecat.Persistence.Sagas.PolecatPersistenceFrameProvider);
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

        // The heart of it. With the in-memory provider the [Entity] parameter was null, the guard
        // stopped the chain, and this document was never touched -- while the host started cleanly.
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
    public async Task polecat_op_writes_through_the_ancillary_store()
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
    public async Task all_and_first_or_default_read_the_ancillary_store()
    {
        // [Entity]'s siblings go through the same provider, so they broke in the same way -- loudly in
        // their case, at codegen, rather than silently.
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

public record AncillaryOnlyCounts(int All, AncillaryOnlyDoc? FirstOrDefault);

[PolecatStore(typeof(IAncillaryOnlyStore))]
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

    public static IPolecatOp Handle(StoreAncillaryOnlyDoc command)
    {
        return PolecatOps.Store(new AncillaryOnlyDoc { Id = command.Id, Name = command.Name });
    }

    public static void Handle(SessionAncillaryOnlyDoc command, IDocumentSession session)
    {
        session.Store(new AncillaryOnlyDoc { Id = command.Id, Name = command.Name });
    }

    public static void Handle(CountAncillaryOnlyDocs _,
        [All] IReadOnlyList<AncillaryOnlyDoc> all,
        [FirstOrDefault] AncillaryOnlyDoc? first)
    {
        LastCounts = new AncillaryOnlyCounts(all.Count, first);
    }
}
