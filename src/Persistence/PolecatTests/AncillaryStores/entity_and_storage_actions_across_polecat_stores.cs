using IntegrationTests;
using JasperFx.Resources;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Polecat;
using Shouldly;
using Wolverine;
using Wolverine.Persistence;
using Wolverine.Polecat;
using Wolverine.Tracking;

namespace PolecatTests.AncillaryStores;

// GH-4462, the other half -- the Polecat mirror of the Marten guard added with GH-4456.
// ancillary_only_host_persistence covers the host with NO main store; this one covers a host with a
// main store and two ancillary stores, each holding a document of the same type under the same id.
// The declarative persistence surface has to read and write through the store the chain was designated
// to and no other -- and "no other" is the assertion that matters, because a provider resolving against
// the wrong store still produces a document somewhere, and a test that only checked the target store
// would stay green.
//
// Both designation styles are covered, since they take different routes to the same chain state:
// [PolecatStore(typeof(T))] through PolecatStoreChainExtensions.UsePolecatStore, and the
// provider-agnostic [Storage(typeof(T))] through the Polecat IAncillaryStoreFrameProvider.
public class entity_and_storage_actions_across_polecat_stores : IAsyncLifetime
{
    private IHost theHost = null!;

    public async ValueTask InitializeAsync()
    {
        theHost = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Durability.MessageStorageSchemaName = "multi_store_wolverine";
                opts.Durability.Mode = DurabilityMode.Solo;
                opts.Policies.AutoApplyTransactions();

                opts.Services.AddPolecat(m =>
                    {
                        m.ConnectionString = Servers.SqlServerConnectionString;
                        m.DatabaseSchemaName = "multi_store_main";
                    })
                    .UseLightweightSessions()
                    .IntegrateWithWolverine();

                opts.Services.AddPolecatStore<IRedStore>(m =>
                    {
                        m.Connection(Servers.SqlServerConnectionString);
                        m.DatabaseSchemaName = "multi_store_red";
                    })
                    .IntegrateWithWolverine();

                opts.Services.AddPolecatStore<IBlueStore>(m =>
                    {
                        m.Connection(Servers.SqlServerConnectionString);
                        m.DatabaseSchemaName = "multi_store_blue";
                    })
                    .IntegrateWithWolverine();

                opts.Discovery.DisableConventionalDiscovery()
                    .IncludeType(typeof(RedHandler))
                    .IncludeType(typeof(BlueHandler));

                opts.Services.AddResourceSetupOnStartup();
            }).StartAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await theHost.StopAsync();
        theHost.Dispose();
    }

    private IDocumentStore theMainStore => theHost.Services.GetRequiredService<IDocumentStore>();
    private IDocumentStore theRedStore => theHost.Services.GetRequiredService<IRedStore>();
    private IDocumentStore theBlueStore => theHost.Services.GetRequiredService<IBlueStore>();

    private static async Task seedAsync(IDocumentStore store, Guid id, string name)
    {
        await using var session = store.LightweightSession();
        session.Store(new MultiStoreDoc { Id = id, Name = name });
        await session.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    private static async Task<MultiStoreDoc?> loadAsync(IDocumentStore store, Guid id)
    {
        await using var session = store.QuerySession();
        return await session.LoadAsync<MultiStoreDoc>(id, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task entity_attribute_loads_from_the_designated_store_only()
    {
        // The same id lives in all three stores with different data, so a provider that resolved against
        // the main store -- or against the wrong ancillary store -- reads a document it should not see
        // and writes the wrong name back.
        var id = Guid.NewGuid();
        await seedAsync(theMainStore, id, "main");
        await seedAsync(theRedStore, id, "red");
        await seedAsync(theBlueStore, id, "blue");

        await theHost.InvokeMessageAndWaitAsync(new RenameInRed(id));

        (await loadAsync(theRedStore, id))!.Name.ShouldBe("red-renamed");

        (await loadAsync(theMainStore, id))!.Name.ShouldBe("main");
        (await loadAsync(theBlueStore, id))!.Name.ShouldBe("blue");
    }

    [Fact]
    public async Task entity_attribute_under_the_agnostic_storage_attribute_loads_from_the_designated_store()
    {
        var id = Guid.NewGuid();
        await seedAsync(theMainStore, id, "main");
        await seedAsync(theRedStore, id, "red");
        await seedAsync(theBlueStore, id, "blue");

        await theHost.InvokeMessageAndWaitAsync(new RenameInBlue(id));

        (await loadAsync(theBlueStore, id))!.Name.ShouldBe("blue-renamed");
        (await loadAsync(theMainStore, id))!.Name.ShouldBe("main");
        (await loadAsync(theRedStore, id))!.Name.ShouldBe("red");
    }

    [Fact]
    public async Task storage_actions_commit_to_the_designated_store_only()
    {
        var id = Guid.NewGuid();
        await theHost.InvokeMessageAndWaitAsync(new InsertInRed(id));

        (await loadAsync(theRedStore, id)).ShouldNotBeNull();
        (await loadAsync(theMainStore, id)).ShouldBeNull();
        (await loadAsync(theBlueStore, id)).ShouldBeNull();
    }

    [Fact]
    public async Task storage_action_delete_removes_from_the_designated_store_only()
    {
        var id = Guid.NewGuid();
        await seedAsync(theMainStore, id, "main");
        await seedAsync(theRedStore, id, "red");
        await seedAsync(theBlueStore, id, "blue");

        await theHost.InvokeMessageAndWaitAsync(new DeleteFromBlue(id));

        (await loadAsync(theBlueStore, id)).ShouldBeNull();
        (await loadAsync(theMainStore, id)).ShouldNotBeNull();
        (await loadAsync(theRedStore, id)).ShouldNotBeNull();
    }

    [Fact]
    public async Task all_reads_only_the_designated_stores_documents()
    {
        // [All] has no id to disambiguate with, so a provider pointed at the wrong store returns a
        // plausible-looking list of the wrong documents.
        var marker = Guid.NewGuid().ToString();
        await seedAsync(theMainStore, Guid.NewGuid(), marker);
        await seedAsync(theMainStore, Guid.NewGuid(), marker);
        await seedAsync(theRedStore, Guid.NewGuid(), marker);

        await theHost.InvokeMessageAndWaitAsync(new CountInRed(marker));

        RedHandler.LastCount.ShouldBe(1);
    }
}

public interface IRedStore : IDocumentStore;

public interface IBlueStore : IDocumentStore;

public class MultiStoreDoc
{
    public Guid Id { get; set; }
    public string Name { get; set; } = null!;
}

public record RenameInRed(Guid Id);

public record InsertInRed(Guid Id);

public record CountInRed(string Marker);

public record RenameInBlue(Guid Id);

public record DeleteFromBlue(Guid Id);

[PolecatStore(typeof(IRedStore))]
public static class RedHandler
{
    public static int LastCount { get; private set; }

    public static IStorageAction<MultiStoreDoc> Handle(RenameInRed command, [Entity] MultiStoreDoc doc)
    {
        // Appending to the name the handler was HANDED, rather than assigning a constant, is what makes
        // this able to tell "read the wrong store" from "wrote to the wrong store".
        doc.Name += "-renamed";
        return Storage.Update(doc);
    }

    public static IStorageAction<MultiStoreDoc> Handle(InsertInRed command)
    {
        return Storage.Insert(new MultiStoreDoc { Id = command.Id, Name = "inserted" });
    }

    public static void Handle(CountInRed command, [All] IReadOnlyList<MultiStoreDoc> all)
    {
        LastCount = all.Count(x => x.Name == command.Marker);
    }
}

[Storage(typeof(IBlueStore))]
public static class BlueHandler
{
    public static IStorageAction<MultiStoreDoc> Handle(RenameInBlue command, [Entity] MultiStoreDoc doc)
    {
        doc.Name += "-renamed";
        return Storage.Update(doc);
    }

    public static IStorageAction<MultiStoreDoc> Handle(DeleteFromBlue command, [Entity] MultiStoreDoc doc)
    {
        return Storage.Delete(doc);
    }
}
