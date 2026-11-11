using IntegrationTests;
using JasperFx;
using JasperFx.Resources;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Polecat;
using Shouldly;
using Wolverine;
using Wolverine.Polecat;
using Wolverine.Polecat.Publishing;
using Wolverine.Runtime;
using Wolverine.Tracking;

namespace PolecatTests.AncillaryStores;

/// <summary>
/// GH-5143: the Polecat twin of Marten's GH-2887. A projection batch on an ANCILLARY Polecat store
/// has to enlist its envelopes in that store's own message store. Before the projection outbox moved
/// into core, Polecat's bridge built its context against the runtime's main message store, so a
/// projection side effect on the ancillary store wrote its envelopes to the main store's tables.
/// </summary>
public class projection_outbox_on_an_ancillary_polecat_store_5143 : IAsyncLifetime
{
    private IHost theHost = null!;

    public async ValueTask InitializeAsync()
    {
        theHost = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Durability.Mode = DurabilityMode.Solo;

                opts.Services.AddPolecat(m =>
                    {
                        m.ConnectionString = Servers.SqlServerConnectionString;
                        m.DatabaseSchemaName = "outbox5143_main";
                    })
                    .IntegrateWithWolverine();

                opts.Services.AddPolecatStore<IProjectionOutbox5143Store>(m =>
                    {
                        m.Connection(Servers.SqlServerConnectionString);
                        m.DatabaseSchemaName = "outbox5143_anc";
                    })
                    .IntegrateWithWolverine(x => x.SchemaName = "outbox5143_anc_wolverine");

                opts.Discovery.DisableConventionalDiscovery();
                opts.Services.AddResourceSetupOnStartup(StartupAction.ResetState);
            }).StartAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await theHost.StopAsync();
        theHost.Dispose();
    }

    [Fact]
    public async Task an_ancillary_store_projection_batch_enlists_in_the_ancillary_message_store()
    {
        var store = theHost.Services.GetRequiredService<IProjectionOutbox5143Store>();
        var runtime = theHost.GetRuntime();

        await using var session = store.LightweightSession();
        var batch = await store.Options.Events.MessageOutbox.CreateBatch(session);

        var storage = batch.ShouldBeOfType<PolecatToWolverineMessageBatch>().Sink.Context.Storage;
        storage.ShouldBeSameAs(runtime.FindAncillaryStoreForMarkerType(typeof(IProjectionOutbox5143Store)));
        storage.ShouldNotBeSameAs(runtime.Storage);
    }

    [Fact]
    public async Task the_main_store_projection_batch_still_enlists_in_the_main_message_store()
    {
        var store = theHost.Services.GetRequiredService<IDocumentStore>();
        var runtime = theHost.GetRuntime();

        await using var session = store.LightweightSession();
        var batch = await store.Options.Events.MessageOutbox.CreateBatch(session);

        batch.ShouldBeOfType<PolecatToWolverineMessageBatch>().Sink.Context.Storage
            .ShouldBeSameAs(runtime.Storage);
    }
}

public interface IProjectionOutbox5143Store : IDocumentStore;
