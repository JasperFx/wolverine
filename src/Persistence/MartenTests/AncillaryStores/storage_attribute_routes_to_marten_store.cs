using IntegrationTests;
using JasperFx.Resources;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine;
using Wolverine.Marten;
using Wolverine.Persistence;
using Wolverine.Tracking;

namespace MartenTests.AncillaryStores;

// GH-3109: the provider-agnostic [Storage(typeof(IMyStore))] attribute must route a handler to a Marten
// ancillary store exactly like [MartenStore], by resolving the Marten IAncillaryStoreFrameProvider from
// the store marker type. The Marten half of the same coverage the Polecat tests give.
public class storage_attribute_routes_to_marten_store : IAsyncLifetime
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

                opts.Services.AddMarten(Servers.PostgresConnectionString).IntegrateWithWolverine();

                opts.Services.AddMartenStore<IStorageAttrStore>(m =>
                    {
                        m.Connection(Servers.PostgresConnectionString);
                        m.DatabaseSchemaName = "storage_attr_players";
                    })
                    .IntegrateWithWolverine();

                opts.Discovery.DisableConventionalDiscovery().IncludeType(typeof(StorageAttrPlayerHandler));
                opts.Services.AddResourceSetupOnStartup();
            }).StartAsync();
    }

    public async Task DisposeAsync()
    {
        await theHost.StopAsync();
        theHost.Dispose();
    }

    [Fact]
    public async Task storage_attribute_opens_and_commits_through_the_marten_ancillary_store()
    {
        var message = new StorageAttrPlayerMessage(Guid.NewGuid().ToString());
        await theHost.InvokeMessageAndWaitAsync(message);

        var store = theHost.DocumentStore<IStorageAttrStore>();
        await using var session = store.QuerySession();
        (await session.LoadAsync<StorageAttrPlayer>(message.Id)).ShouldNotBeNull();
    }
}

public interface IStorageAttrStore : IDocumentStore;

public class StorageAttrPlayer
{
    public string Id { get; set; } = null!;
}

public record StorageAttrPlayerMessage(string Id);

// The provider-agnostic [Storage] attribute routes this handler to the Marten ancillary store.
[Storage(typeof(IStorageAttrStore))]
public static class StorageAttrPlayerHandler
{
    public static IMartenOp Handle(StorageAttrPlayerMessage message)
    {
        return MartenOps.Store(new StorageAttrPlayer { Id = message.Id });
    }
}
