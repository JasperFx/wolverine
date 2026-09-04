using JasperFx.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine;
using Wolverine.ComplianceTests;
using Wolverine.CosmosDb;

namespace CosmosDbTests;

// GH-4286: the recovery loop's hourly throttle reset its own timestamp at the top of every
// iteration and compared against it a few statements later, so the expired dead letter sweep
// could only ever fire if a single recovery tick took more than an hour — with
// DeadLetterQueueExpirationEnabled on, dead letters accumulated without bound. The throttle
// now lives outside the loop, so the FIRST recovery tick sweeps.
[Collection("cosmosdb")]
public class Bug_4286_dead_letter_expiration
{
    private readonly AppFixture _fixture;

    public Bug_4286_dead_letter_expiration(AppFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task expired_dead_letters_are_swept_by_the_recovery_loop()
    {
        await _fixture.ClearAll();
        var store = _fixture.BuildMessageStore();

        // Seed the dead letter queue BEFORE the host starts so the first recovery tick sees both
        var expired = ObjectMother.Envelope();
        expired.DeliverBy = DateTimeOffset.UtcNow.AddMinutes(-5);
        await store.Inbox.MoveToDeadLetterStorageAsync(expired, new InvalidOperationException("expired"));

        var keeper = ObjectMother.Envelope();
        keeper.DeliverBy = DateTimeOffset.UtcNow.AddDays(1);
        await store.Inbox.MoveToDeadLetterStorageAsync(keeper, new InvalidOperationException("keeper"));

        (await store.Admin.FetchCountsAsync()).DeadLetter.ShouldBe(2);

        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Durability.Mode = DurabilityMode.Solo;
                opts.Durability.DeadLetterQueueExpirationEnabled = true;
                opts.Durability.ScheduledJobFirstExecution = 0.Seconds();
                opts.Durability.ScheduledJobPollingTime = 1.Seconds();
                opts.UseCosmosDbPersistence(AppFixture.DatabaseName);
                opts.Services.AddSingleton(_fixture.Client);
                opts.ServiceName = "dlq-expiration";
            }).StartAsync(TestContext.Current.CancellationToken);

        var deadline = DateTimeOffset.UtcNow.AddSeconds(30);
        while (DateTimeOffset.UtcNow < deadline)
        {
            var counts = await store.Admin.FetchCountsAsync();
            if (counts.DeadLetter <= 1) break;

            await Task.Delay(250.Milliseconds(), TestContext.Current.CancellationToken);
        }

        (await store.Admin.FetchCountsAsync()).DeadLetter.ShouldBe(1);
        (await store.DeadLetters.DeadLetterEnvelopeByIdAsync(keeper.Id)).ShouldNotBeNull();
        (await store.DeadLetters.DeadLetterEnvelopeByIdAsync(expired.Id)).ShouldBeNull();

        await host.StopAsync(TestContext.Current.CancellationToken);
    }
}
