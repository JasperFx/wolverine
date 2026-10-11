using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine;
using Wolverine.ComplianceTests;
using Wolverine.CosmosDb;
using Wolverine.CosmosDb.Internals;
using Wolverine.Tracking;
using Wolverine.Transports.Tcp;

namespace CosmosDbTests;

/// <summary>
/// GH-4216 1a. The shared message store contract under <see cref="MessageIdentity.IdAndDestination"/>, the identity
/// shape the GH-4209 class of defect depends on. PostgreSQL, SQL Server, Oracle, MySQL, SQLite and RavenDb already
/// answer it; CosmosDb keys its inbox documents by <c>IdentityFor(envelope)</c> the same way, and now does too.
/// Running it found that the identity carried the destination's slashes into the Cosmos document id, which
/// Cosmos does not allow: the documents were created, but every point read, replace and delete missed them.
/// </summary>
[Collection("cosmosdb")]
public class message_store_compliance_with_id_and_destination_identity : MessageStoreCompliance
{
    private readonly AppFixture _fixture;

    public message_store_compliance_with_id_and_destination_identity(AppFixture fixture)
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
                opts.Durability.MessageIdentity = MessageIdentity.IdAndDestination;

                opts.UseCosmosDbPersistence(AppFixture.DatabaseName);
                opts.Services.AddSingleton(_fixture.Client);

                opts.ListenAtPort(2345).UseDurableInbox();
            }).StartAsync();

        return host;
    }

    [Fact]
    public void the_identity_is_a_legal_cosmos_document_id()
    {
        var envelope = ObjectMother.Envelope();
        envelope.Destination = new Uri("rabbitmq://queue/orders?durable=true#tag");

        var id = theHost.GetRuntime().Storage.ShouldBeOfType<CosmosDbMessageStore>().IdentityFor(envelope);

        id.ShouldStartWith($"incoming|{envelope.Id}|");
        id.IndexOfAny(['/', '\\', '?', '#']).ShouldBe(-1);
    }
}
