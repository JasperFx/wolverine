using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Raven.Client.Documents;
using Raven.Client.Documents.Operations;
using Raven.Client.Documents.Queries;
using Raven.Embedded;
using Wolverine;
using Wolverine.ComplianceTests;
using Wolverine.RavenDb;
using Wolverine.RavenDb.Internals;
using Wolverine.Transports.Tcp;

namespace RavenDbTests;

public class DatabaseFixture : IAsyncLifetime
{
    public Task InitializeAsync()
    {
        EmbeddedServer.Instance.StartServer();
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        EmbeddedServer.Instance.Dispose();
        return Task.CompletedTask;
    }
}

[CollectionDefinition("raven")]
public class DatabaseCollection : ICollectionFixture<DatabaseFixture>
{
    // This class has no code, and is never created. Its purpose is simply
    // to be the place to apply [CollectionDefinition] and all the
    // ICollectionFixture<> interfaces.
}

[Collection("raven")]
public class message_store_compliance : MessageStoreCompliance
{
    public message_store_compliance(DatabaseFixture fixture)
    {
    }

    public override async Task<IHost> BuildCleanHost()
    {
        var store = await EmbeddedServer.Instance.GetDocumentStoreAsync(Guid.NewGuid().ToString());

        var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                // TODO -- TEMP!
                opts.Durability.Mode = DurabilityMode.Solo;
                
                opts.UseRavenDbPersistence();
                opts.Services.AddSingleton<IDocumentStore>(store);

                opts.ListenAtPort(2345).UseDurableInbox();
            }).StartAsync();
        
        return host;
    }


}