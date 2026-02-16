using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Wolverine;
using Wolverine.ComplianceTests;
using Wolverine.CosmosDb;
using Wolverine.CosmosDb.Internals;
using Wolverine.Transports.Tcp;

namespace CosmosDbTests;

[Collection("cosmosdb")]
public class message_store_compliance : MessageStoreCompliance
{
    private readonly AppFixture _fixture;

    public message_store_compliance(AppFixture fixture)
    {
        _fixture = fixture;
    }

    public override async Task<IHost> BuildCleanHost()
    {
        await _fixture.ClearAll();

        var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Durability.Mode = DurabilityMode.Solo;

                opts.UseCosmosDbPersistence(AppFixture.DatabaseName);
                opts.Services.AddSingleton(_fixture.Client);

                opts.ListenAtPort(2345).UseDurableInbox();
            }).StartAsync();

        return host;
    }
}
