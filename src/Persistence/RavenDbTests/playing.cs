using Raven.Embedded;
using Shouldly;
using Wolverine.ComplianceTests;
using Wolverine.RavenDb.Internals;

namespace RavenDbTests;

public class playing
{
    [Fact]
    public async Task try_to_persist_envelope()
    {
        EmbeddedServer.Instance.StartServer();
        using var store = await EmbeddedServer.Instance.GetDocumentStoreAsync("Testing");
        
        var envelope = ObjectMother.Envelope();
        var incoming = new IncomingMessage(envelope);

        using var session1 = store.OpenAsyncSession();
        await session1.StoreAsync(incoming);
        await session1.SaveChangesAsync();

        using var session2 = store.OpenAsyncSession();
        var incoming2 = await session2.LoadAsync<IncomingMessage>(incoming.Id, CancellationToken.None);
        
        incoming2.Status.ShouldBe(incoming.Status);
        incoming2.OwnerId.ShouldBe(incoming.OwnerId);
        incoming2.MessageType.ShouldBe(incoming.MessageType);
        incoming2.Body.ShouldBe(incoming.Body);
    }
}