using JasperFx.Core;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine;
using Wolverine.ComplianceTests;
using Wolverine.CosmosDb;
using Wolverine.CosmosDb.Internals;

namespace CosmosDbTests;

// GH-4509: MarkIncomingEnvelopeAsHandledAsync stamped a keepUntil that nothing ever read back. There was
// no equivalent of the RDBMS DeleteExpiredHandledEnvelopesCommand, no ttl is written, and the container is
// created with default ContainerProperties (TTL off) -- so handled inbox documents were kept forever,
// bodies included, since mark-handled is a read/modify/replace that leaves body alone.
//
// That bites harder here than on a SQL store: IncomingMessage.PartitionKey is the envelope's destination,
// so every handled envelope for a listening endpoint piles into ONE logical partition, against a 20 GB hard
// ceiling and a 10k RU/s cap. FetchCountsAsync, which CheckHealthAsync calls on every health check, counts
// that pile cross-partition as well.
[Collection("cosmosdb")]
public class Bug_4509_handled_envelope_expiration
{
    private readonly AppFixture _fixture;

    public Bug_4509_handled_envelope_expiration(AppFixture fixture)
    {
        _fixture = fixture;
    }

    /// <summary>
    /// A store whose <c>KeepAfterMessageHandling</c> is in the PAST, so that marking an envelope handled
    /// stamps an already-expired <c>keepUntil</c>. The default is five minutes, and waiting that out in a
    /// test is not an option.
    /// </summary>
    private CosmosDbMessageStore StoreWithRetention(TimeSpan keepAfterHandling)
    {
        var options = new WolverineOptions();
        options.Durability.KeepAfterMessageHandling = keepAfterHandling;

        return new CosmosDbMessageStore(_fixture.Client, AppFixture.DatabaseName, _fixture.Container, options);
    }

    /// <summary>
    /// Whether THIS envelope's inbox document is still there.
    /// </summary>
    /// <remarks>
    /// Asked by envelope id rather than through <c>FetchCountsAsync().Handled</c> deliberately. A running
    /// Wolverine host handles its own control traffic, so it mints fresh handled documents of its own with
    /// the default five-minute retention while the test watches — a count therefore never reaches zero and
    /// says nothing about whether the seeded document was swept. Same trap as GH-4499: a count does not tell
    /// you whether the rows are the same rows.
    /// </remarks>
    private async Task<bool> ExistsAsync(Guid envelopeId)
    {
        var query = new QueryDefinition(
                "SELECT VALUE COUNT(1) FROM c WHERE c.docType = @docType AND c.envelopeId = @envelopeId")
            .WithParameter("@docType", DocumentTypes.Incoming)
            .WithParameter("@envelopeId", envelopeId);

        using var iterator = _fixture.Container.GetItemQueryIterator<int>(query);

        var total = 0;
        while (iterator.HasMoreResults)
        {
            foreach (var count in await iterator.ReadNextAsync(TestContext.Current.CancellationToken))
            {
                total += count;
            }
        }

        return total > 0;
    }

    [Fact]
    public async Task expired_handled_envelopes_are_swept_by_the_recovery_loop()
    {
        await _fixture.ClearAll();

        // Seeded BEFORE the host starts, so the first recovery tick sees them -- the same shape as the
        // GH-4286 dead letter sibling
        var expired = ObjectMother.Envelope();
        await StoreWithRetention(-1.Minutes()).Inbox.StoreIncomingAsync(expired);
        await StoreWithRetention(-1.Minutes()).Inbox.MarkIncomingEnvelopeAsHandledAsync(expired);

        // Handled, but still well inside its window. Without this, the test would pass over a sweep that
        // deleted every handled document on sight rather than reading keepUntil.
        var retained = ObjectMother.Envelope();
        await StoreWithRetention(30.Minutes()).Inbox.StoreIncomingAsync(retained);
        await StoreWithRetention(30.Minutes()).Inbox.MarkIncomingEnvelopeAsHandledAsync(retained);

        // Never handled at all: the sweep must key off status as well as keepUntil
        var pending = ObjectMother.Envelope();
        await _fixture.BuildMessageStore().Inbox.StoreIncomingAsync(pending);

        (await ExistsAsync(expired.Id)).ShouldBeTrue();

        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Durability.Mode = DurabilityMode.Solo;
                opts.Durability.ScheduledJobFirstExecution = 0.Seconds();
                opts.Durability.ScheduledJobPollingTime = 1.Seconds();

                // The knobs GH-4509 notes were referenced only from Wolverine.RDBMS.DurabilityAgent, so on
                // Cosmos they did nothing at all before this
                opts.Durability.HandledMessageCleanupPollingTime = 1.Seconds();

                opts.UseCosmosDbPersistence(AppFixture.DatabaseName);
                opts.Services.AddSingleton(_fixture.Client);
                opts.ServiceName = "handled-expiration";
            }).StartAsync(TestContext.Current.CancellationToken);

        var deadline = DateTimeOffset.UtcNow.AddSeconds(30);
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (!await ExistsAsync(expired.Id)) break;
            await Task.Delay(250.Milliseconds(), TestContext.Current.CancellationToken);
        }

        // The expired one is gone...
        (await ExistsAsync(expired.Id)).ShouldBeFalse();

        // ...and neither of the other two was touched
        (await ExistsAsync(retained.Id)).ShouldBeTrue();
        (await ExistsAsync(pending.Id)).ShouldBeTrue();

        await host.StopAsync(TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// GH-4509: <c>DeleteAllHandledAsync</c> threw <c>NotSupportedException</c>, so the built-in
    /// <c>clear-handled</c> command failed outright — there was no way to clean up after the fact on the one
    /// provider that never swept in the first place.
    /// </summary>
    [Fact]
    public async Task delete_all_handled_clears_handled_and_leaves_everything_else()
    {
        await _fixture.ClearAll();
        var store = _fixture.BuildMessageStore();

        // Deliberately INSIDE its retention window: this verb clears handled envelopes outright, unlike the
        // timed sweep, so keepUntil must not enter into it
        var handled = ObjectMother.Envelope();
        await StoreWithRetention(30.Minutes()).Inbox.StoreIncomingAsync(handled);
        await StoreWithRetention(30.Minutes()).Inbox.MarkIncomingEnvelopeAsHandledAsync(handled);

        var pending = ObjectMother.Envelope();
        await store.Inbox.StoreIncomingAsync(pending);

        var outgoing = ObjectMother.Envelope();
        await store.Outbox.StoreOutgoingAsync(outgoing, 0);

        await store.Admin.DeleteAllHandledAsync();

        (await ExistsAsync(handled.Id)).ShouldBeFalse();
        (await ExistsAsync(pending.Id)).ShouldBeTrue();

        // No host is running here, so nothing else is minting documents and a count is safe
        (await store.Admin.FetchCountsAsync()).Outgoing.ShouldBe(1);
    }
}
