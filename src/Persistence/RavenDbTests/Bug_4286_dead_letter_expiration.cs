using JasperFx.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Raven.Client.Documents;
using Raven.Client.Documents.Linq;
using Shouldly;
using Wolverine;
using Wolverine.ComplianceTests;
using Wolverine.RavenDb;
using Wolverine.RavenDb.Internals;

namespace RavenDbTests;

// GH-4286: the recovery loop's hourly throttle reset its own timestamp at the top of every
// iteration and compared against it a few statements later, so the expired dead letter sweep
// could only ever fire if a single recovery tick took more than an hour — with
// DeadLetterQueueExpirationEnabled on, dead letters accumulated without bound. The throttle
// now lives outside the loop, so the FIRST recovery tick sweeps.
[Collection("raven")]
public class Bug_4286_dead_letter_expiration
{
    private readonly DatabaseFixture _fixture;

    public Bug_4286_dead_letter_expiration(DatabaseFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task expired_dead_letters_are_swept_by_the_recovery_loop()
    {
        var documentStore = _fixture.StartRavenStore();
        var store = new RavenDbMessageStore(documentStore, new WolverineOptions());

        // Seed the dead letter queue BEFORE the host starts so the first recovery tick sees both
        var expired = ObjectMother.Envelope();
        expired.DeliverBy = DateTimeOffset.UtcNow.AddMinutes(-5);
        await store.Inbox.MoveToDeadLetterStorageAsync(expired, new InvalidOperationException("expired"));

        var keeper = ObjectMother.Envelope();
        keeper.DeliverBy = DateTimeOffset.UtcNow.AddDays(1);
        await store.Inbox.MoveToDeadLetterStorageAsync(keeper, new InvalidOperationException("keeper"));

        // Prime the ExpirationTime auto index and wait until both seeded documents are queryable,
        // so the sweep's own dynamic query on the first recovery tick is not racing a stale index
        using (var session = documentStore.OpenAsyncSession())
        {
            var seeded = await session.Query<DeadLetterMessage>()
                .Customize(x => x.WaitForNonStaleResults())
                .Where(x => x.ExpirationTime < DateTimeOffset.UtcNow.AddDays(2))
                .CountAsync(TestContext.Current.CancellationToken);
            seeded.ShouldBe(2);
        }

        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Durability.Mode = DurabilityMode.Solo;
                opts.Durability.DeadLetterQueueExpirationEnabled = true;
                opts.Durability.ScheduledJobFirstExecution = 0.Seconds();
                opts.Durability.ScheduledJobPollingTime = 1.Seconds();
                opts.Services.AddSingleton<IDocumentStore>(documentStore);
                opts.UseRavenDbPersistence();
                opts.ServiceName = "dlq-expiration";
            }).StartAsync(TestContext.Current.CancellationToken);

        var deadline = DateTimeOffset.UtcNow.AddSeconds(30);
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (await store.DeadLetters.DeadLetterEnvelopeByIdAsync(expired.Id) == null) break;

            await Task.Delay(250.Milliseconds(), TestContext.Current.CancellationToken);
        }

        (await store.DeadLetters.DeadLetterEnvelopeByIdAsync(expired.Id)).ShouldBeNull();
        (await store.DeadLetters.DeadLetterEnvelopeByIdAsync(keeper.Id)).ShouldNotBeNull();

        await host.StopAsync(TestContext.Current.CancellationToken);
    }
}
