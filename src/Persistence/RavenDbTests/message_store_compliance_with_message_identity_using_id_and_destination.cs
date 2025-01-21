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
    private IDocumentStore _store;

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

    [Fact]
    public async Task marks_envelope_as_having_an_expires_on_mark_handled()
    {
        var envelope = ObjectMother.Envelope();

        await thePersistence.Inbox.StoreIncomingAsync(envelope);
        await thePersistence.Inbox.MarkIncomingEnvelopeAsHandledAsync(envelope);

        using var session = _store.OpenAsyncSession();
        var incoming = await session.LoadAsync<IncomingMessage>(theHost.GetRuntime().Storage.As<RavenDbMessageStore>().IdentityFor(envelope));
        var metadata = session.Advanced.GetMetadataFor(incoming);
        metadata.TryGetValue("@expires", out var raw).ShouldBeTrue();
        
        var value = metadata["@expires"];
        Debug.WriteLine(value);

    }


}