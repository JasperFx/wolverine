using Fisher;
using JasperFx;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine;
using Wolverine.Fisher;
using Wolverine.Persistence;
using Wolverine.Persistence.Sagas;
using Wolverine.Runtime;
using Wolverine.Tracking;

namespace FisherTests;

/// <summary>
///     GH-4463, the Fisher twin of GH-4456. A host whose only Fisher stores are ancillary --
///     <c>AddFisherStore&lt;T&gt;().IntegrateWithWolverine()</c> with no <c>AddFisher(...)</c> -- never
///     registered <c>FisherIntegration</c>, and that extension is what calls
///     <c>InsertFirstPersistenceStrategy&lt;FisherPersistenceFrameProvider&gt;()</c>. So <c>[Entity]</c>
///     fell through to the catch-all <c>InMemoryPersistenceFrameProvider</c> and read the document out of
///     an in-memory dictionary nothing ever populates: always null, the not-null guard stopped the chain,
///     and the handler never ran with no exception and nothing logged.
/// </summary>
/// <remarks>
///     Fisher is the store where this configuration is least exotic. A Fisher store IS a SQLite file, so
///     "one store per module, no main store" is close to the natural way to lay out a Fisher modular
///     monolith -- each module already wants its own file.
///     <para>
///     These pin the whole declarative-persistence surface, not just <c>[Entity]</c>, because it all
///     resolves through the same missing provider.
///     </para>
/// </remarks>
public class ancillary_only_host_persistence : IAsyncLifetime
{
    private FisherTestDatabase theDatabase = null!;
    private IHost theHost = null!;

    public async ValueTask InitializeAsync()
    {
        theDatabase = Servers.CreateDatabase("ancillary_only");

        theHost = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Discovery.DisableConventionalDiscovery()
                    .IncludeType(typeof(AncillaryOnlyHandler));

                opts.Durability.Mode = DurabilityMode.Solo;
                opts.Policies.AutoApplyTransactions();

                // Deliberately NO AddFisher(...). This host's only Fisher store is the ancillary one.
                opts.Services.AddFisherStore<IAncillaryOnlyStore>(m =>
                    {
                        m.Connection(theDatabase.ConnectionString);
                        m.AutoCreateSchemaObjects = AutoCreate.All;
                    })
                    .ApplyAllDatabaseChangesOnStartup()
                    .IntegrateWithWolverine();
            }).StartAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await theHost.StopAsync();
        theHost.Dispose();
        theDatabase.Dispose();
    }

    private IDocumentStore theStore => theHost.Services.GetRequiredService<IAncillaryOnlyStore>();

    [Fact]
    public void the_fisher_persistence_strategy_is_registered()
    {
        // The root cause, asserted directly, so a regression reads as one legible failure rather than
        // five confusing ones.
        var runtime = theHost.Services.GetRequiredService<IWolverineRuntime>();
        runtime.Options.CodeGeneration.PersistenceProviders()
            .ShouldContain(x => x is Wolverine.Fisher.Persistence.Sagas.FisherPersistenceFrameProvider);
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
    public async Task fisher_op_writes_through_the_ancillary_store()
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
        // [Entity]'s siblings go through the same provider, so they broke in the same way.
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

[FisherStore(typeof(IAncillaryOnlyStore))]
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

    public static IFisherOp Handle(StoreAncillaryOnlyDoc command)
    {
        return FisherOps.Store(new AncillaryOnlyDoc { Id = command.Id, Name = command.Name });
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
