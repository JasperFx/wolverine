using System.Diagnostics;
using JasperFx.Core.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Raven.Client.Documents;
using Shouldly;
using Wolverine;
using Wolverine.ComplianceTests;
using Wolverine.RavenDb;
using Wolverine.RavenDb.Internals;
using Wolverine.Tracking;
using Wolverine.Transports.Tcp;

namespace RavenDbTests;

[Collection("raven")]
public class message_store_compliance_with_message_identity_using_id_and_destination : MessageStoreCompliance
{
    private readonly DatabaseFixture _fixture;
    private IDocumentStore _store = null!;

    public message_store_compliance_with_message_identity_using_id_and_destination(DatabaseFixture fixture)
    {
        _fixture = fixture;
    }

    public override async Task<IHost> BuildCleanHost()
    {
        var store = _fixture.StartRavenStore();
        _store = store;

        var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                // TODO -- TEMP!
                opts.Durability.Mode = DurabilityMode.Solo;

                opts.Durability.MessageIdentity = MessageIdentity.IdAndDestination;
                
                opts.UseRavenDbPersistence();
                opts.Services.AddSingleton<IDocumentStore>(store);

                opts.ListenAtPort(2345).UseDurableInbox();
            }).StartAsync();
        
        return host;
    }

    // GH-4216. RavenDB treats a document id ending in '/' or '|' as a request for a server-generated id, and the
    // IdAndDestination identity carries the destination Uri into the id. The CosmosDb twin of this test found
    // the identity's slashes made every Cosmos point read miss; here the slashes are legal, so this pins the
    // two characters RavenDB does care about and that a stored envelope is found again by its identity
    [Theory]
    [InlineData("tcp://localhost:2345/")]
    [InlineData("rabbitmq://queue/orders")]
    [InlineData("rabbitmq://queue/orders/")]
    [InlineData("rabbitmq://queue/orders?durable=true#tag")]
    public async Task the_identity_is_a_usable_ravendb_document_id(string destination)
    {
        var envelope = ObjectMother.Envelope();
        envelope.Destination = new Uri(destination);

        var id = theHost.GetRuntime().Storage.As<RavenDbMessageStore>().IdentityFor(envelope);

        id.ShouldStartWith($"{envelope.Id}/");
        id.ShouldNotEndWith("/");
        id.ShouldNotEndWith("|");

        await thePersistence.Inbox.StoreIncomingAsync(envelope);
        (await thePersistence.Inbox.ExistsAsync(envelope, CancellationToken.None)).ShouldBeTrue();

        await thePersistence.Inbox.MarkIncomingEnvelopeAsHandledAsync(envelope);

        using var session = _store.OpenAsyncSession();
        var incoming = await session.LoadAsync<IncomingMessage>(id, TestContext.Current.CancellationToken);
        incoming.ShouldNotBeNull();
        incoming.Status.ShouldBe(EnvelopeStatus.Handled);
    }

    [Fact]
    public async Task marks_envelope_as_having_an_expires_on_mark_handled()
    {
        var envelope = ObjectMother.Envelope();

        await thePersistence.Inbox.StoreIncomingAsync(envelope);
        await thePersistence.Inbox.MarkIncomingEnvelopeAsHandledAsync(envelope);

        using var session = _store.OpenAsyncSession();
        var incoming = await session.LoadAsync<IncomingMessage>(theHost.GetRuntime().Storage.As<RavenDbMessageStore>().IdentityFor(envelope), TestContext.Current.CancellationToken);
        var metadata = session.Advanced.GetMetadataFor(incoming);
        metadata.TryGetValue("@expires", out var raw).ShouldBeTrue();
        
        var value = metadata["@expires"];
        Debug.WriteLine(value);

    }


}