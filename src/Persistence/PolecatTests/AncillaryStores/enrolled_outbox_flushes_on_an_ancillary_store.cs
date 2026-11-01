using IntegrationTests;
using JasperFx.Core;
using JasperFx.Resources;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Polecat;
using Wolverine;
using Wolverine.Polecat;
using Wolverine.Runtime;
using Wolverine.Tracking;

namespace PolecatTests.AncillaryStores;

/// <summary>
/// The flush an enrolled session gets on commit comes from a listener on the session's store, not from the
/// session, so a store that was not given one looks exactly like the bug in
/// <see cref="PolecatTests.enrolled_outbox_flushes_on_commit" /> again.
/// </summary>
public class enrolled_outbox_flushes_on_an_ancillary_store : IAsyncLifetime
{
    private const string MainSchema = "anc_enrolled_main";
    private const string StoreSchema = "anc_enrolled_store";

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private IHost theHost = null!;

    public async ValueTask InitializeAsync()
    {
        theHost = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Durability.Mode = DurabilityMode.Solo;
                opts.Durability.DurabilityAgentEnabled = false;

                opts.Discovery.DisableConventionalDiscovery().IncludeType(typeof(EnrolledOutboxHandler));

                opts.Services.AddPolecat(m =>
                    {
                        m.ConnectionString = Servers.SqlServerConnectionString;
                        m.DatabaseSchemaName = MainSchema;
                    })
                    .IntegrateWithWolverine(x => x.MessageStorageSchemaName = MainSchema);

                opts.Services.AddPolecatStore<IThingStore>(m =>
                    {
                        m.ConnectionString = Servers.SqlServerConnectionString;
                        m.DatabaseSchemaName = StoreSchema;
                    })
                    .IntegrateWithWolverine(x => x.SchemaName = StoreSchema);

                opts.PublishMessage<EnrolledBufferedLocal>().ToLocalQueue("enrolled-ancillary").BufferedInMemory();

                opts.Services.AddResourceSetupOnStartup();
            }).StartAsync(Token);

        await theHost.ResetResourceState();
    }

    public async ValueTask DisposeAsync()
    {
        await theHost.StopAsync();
        theHost.Dispose();
    }

    [Fact]
    public async Task a_session_from_an_ancillary_store_flushes_on_commit()
    {
        var id = Guid.NewGuid();

        await using var session = theHost.Services.GetRequiredService<IThingStore>().LightweightSession();
        var outbox = new PolecatOutbox(theHost.GetRuntime(), session);

        session.Store(new Thing { Id = id.ToString() });
        await outbox.PublishAsync(new EnrolledBufferedLocal(id));

        await session.SaveChangesAsync(Token);

        await EnrolledDeliveries.WaitFor(id, 15.Seconds());
    }
}
