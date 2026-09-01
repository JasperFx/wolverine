using Microsoft.Extensions.Hosting;
using Wolverine;
using Wolverine.ComplianceTests;
using Wolverine.CosmosDb;
using Wolverine.Transports.Tcp;
using Microsoft.Extensions.DependencyInjection;

namespace CosmosDbTests;

/// <summary>
/// GH-4216. The whole shared suite under <see cref="MessageIdentity.IdAndDestination"/> rather than the
/// default <see cref="MessageIdentity.IdOnly"/>, which only PostgreSQL, SQL Server and RavenDb answered
/// before. <see cref="Wolverine.CosmosDb.Internals.CosmosDbMessageStore"/> picks its identity strategy off
/// this setting exactly as the RDBMS stores do, so the composite shape deserves the same coverage: GH-4209
/// was identity-shape specific end to end, and a store that never runs the suite under it cannot report that
/// class of bug.
/// </summary>
[Collection("cosmosdb")]
public class message_store_compliance_with_id_and_destination : MessageStoreCompliance
{
    private readonly AppFixture _fixture;

    public message_store_compliance_with_id_and_destination(AppFixture fixture)
    {
        _fixture = fixture;
    }

    public override async Task<IHost> BuildCleanHost()
    {
        await _fixture.ClearAll();

        return await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Durability.Mode = DurabilityMode.Solo;
                opts.Durability.MessageIdentity = MessageIdentity.IdAndDestination;

                opts.UseCosmosDbPersistence(AppFixture.DatabaseName);
                opts.Services.AddSingleton(_fixture.Client);

                opts.ListenAtPort(2346).UseDurableInbox();
            }).StartAsync();
    }
}
