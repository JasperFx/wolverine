using Fisher;
using JasperFx;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine;
using Wolverine.Fisher;
using Wolverine.Fisher.Publishing;
using Wolverine.Runtime;
using Wolverine.Tracking;

namespace FisherTests;

/// <summary>
/// GH-5143: the Fisher twin of Marten's GH-2887. A projection batch on an ANCILLARY Fisher store
/// has to enlist its envelopes in that store's own message store, which is a different SQLite file.
/// Before the projection outbox moved into core, Fisher's bridge built its context against the
/// runtime's main message store.
/// </summary>
public class projection_outbox_on_an_ancillary_fisher_store_5143 : IAsyncLifetime
{
    private FisherTestDatabase theMainDatabase = null!;
    private FisherTestDatabase theAncillaryDatabase = null!;
    private IHost theHost = null!;

    public async ValueTask InitializeAsync()
    {
        theMainDatabase = Servers.CreateDatabase("outbox5143_main");
        theAncillaryDatabase = Servers.CreateDatabase("outbox5143_anc");

        theHost = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Durability.Mode = DurabilityMode.Solo;
                opts.Discovery.DisableConventionalDiscovery();

                opts.Services.AddFisher(m =>
                    {
                        m.Connection(theMainDatabase.ConnectionString);
                        m.AutoCreateSchemaObjects = AutoCreate.All;
                    })
                    .ApplyAllDatabaseChangesOnStartup()
                    .IntegrateWithWolverine();

                opts.Services.AddFisherStore<IProjectionOutbox5143FisherStore>(m =>
                    {
                        m.Connection(theAncillaryDatabase.ConnectionString);
                        m.AutoCreateSchemaObjects = AutoCreate.All;
                    })
                    .ApplyAllDatabaseChangesOnStartup()
                    .IntegrateWithWolverine(x => x.SchemaName = "outbox5143_anc");
            }).StartAsync(TestContext.Current.CancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        await theHost.StopAsync();
        theHost.Dispose();
        theAncillaryDatabase.Dispose();
        theMainDatabase.Dispose();
    }

    [Fact]
    public async Task an_ancillary_store_projection_batch_enlists_in_the_ancillary_message_store()
    {
        var store = theHost.Services.GetRequiredService<IProjectionOutbox5143FisherStore>();
        var runtime = theHost.GetRuntime();

        await using var session = store.LightweightSession();
        var batch = await store.Options.Events.MessageOutbox.CreateBatch(session);

        var storage = batch.ShouldBeOfType<FisherToWolverineMessageBatch>().Sink.Context.Storage;
        storage.ShouldBeSameAs(runtime.FindAncillaryStoreForMarkerType(typeof(IProjectionOutbox5143FisherStore)));
        storage.ShouldNotBeSameAs(runtime.Storage);
    }

    [Fact]
    public async Task the_main_store_projection_batch_still_enlists_in_the_main_message_store()
    {
        var store = theHost.Services.GetRequiredService<IDocumentStore>();
        var runtime = theHost.GetRuntime();

        await using var session = store.LightweightSession();
        var batch = await store.Options.Events.MessageOutbox.CreateBatch(session);

        batch.ShouldBeOfType<FisherToWolverineMessageBatch>().Sink.Context.Storage
            .ShouldBeSameAs(runtime.Storage);
    }
}

public interface IProjectionOutbox5143FisherStore : IDocumentStore;
