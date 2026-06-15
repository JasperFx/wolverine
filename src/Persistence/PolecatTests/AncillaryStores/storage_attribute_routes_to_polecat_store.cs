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

// GH-3109: the provider-agnostic [Storage(typeof(IMyStore))] attribute must route a handler to a
// Polecat ancillary store exactly like [PolecatStore], by resolving the Polecat
// IAncillaryStoreFrameProvider from the store marker type.
public class storage_attribute_routes_to_polecat_store : IAsyncLifetime
{
    private IHost theHost = null!;

    public async Task InitializeAsync()
    {
        theHost = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Durability.MessageStorageSchemaName = "wolverine";
                opts.Durability.Mode = DurabilityMode.Solo;
                opts.Policies.AutoApplyTransactions();

                opts.Services.AddPolecat(m =>
                    {
                        m.ConnectionString = Servers.SqlServerConnectionString;
                        m.DatabaseSchemaName = "storage_attr_main";
                    })
                    .UseLightweightSessions()
                    .IntegrateWithWolverine();

                opts.Services.AddPolecatStore<IPlayerStore>(m =>
                    {
                        m.Connection(Servers.SqlServerConnectionString);
                        m.DatabaseSchemaName = "storage_attr_players";
                    })
                    .IntegrateWithWolverine();

                opts.Discovery.DisableConventionalDiscovery().IncludeType(typeof(StoragePlayerHandler));
                opts.Services.AddResourceSetupOnStartup();
            }).StartAsync();
    }

    public async Task DisposeAsync()
    {
        await theHost.StopAsync();
        theHost.Dispose();
    }

    [Fact]
    public async Task storage_attribute_opens_and_commits_through_the_polecat_ancillary_store()
    {
        var message = new StoragePlayerMessage(Guid.NewGuid().ToString());
        await theHost.InvokeMessageAndWaitAsync(message);

        var store = theHost.Services.GetRequiredService<IPlayerStore>();
        await using var session = store.QuerySession();
        (await session.LoadAsync<Player>(message.Id)).ShouldNotBeNull();
    }
}

public record StoragePlayerMessage(string Id);

#region sample_generic_storage_attribute_handler
// The provider-agnostic [Storage] attribute — works for Polecat, Marten, ... — routes this handler to
// the IPlayerStore ancillary store.
[Storage(typeof(IPlayerStore))]
public static class StoragePlayerHandler
{
    public static void Handle(StoragePlayerMessage message, IDocumentSession session)
    {
        session.Store(new Player { Id = message.Id });
    }
}

#endregion
