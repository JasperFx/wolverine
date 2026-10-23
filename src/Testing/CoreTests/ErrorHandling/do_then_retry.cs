using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine;
using Wolverine.ErrorHandling;
using Wolverine.Tracking;
using Xunit;

namespace CoreTests.ErrorHandling;

// GH-5068. DoThenRetry() runs a recovery action and then retries the message, and it does the right
// thing for both a message received from a queue and a message executed inline by IMessageBus.InvokeAsync()
public class do_then_retry : IAsyncLifetime
{
    private readonly PriceCache theCache = new();
    private IHost _host = null!;

    public async ValueTask InitializeAsync()
    {
        #region sample_do_then_retry

        _host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Discovery.DisableConventionalDiscovery();
                opts.Discovery.IncludeType(typeof(PriceLookupHandler));
                opts.Services.AddSingleton(theCache);

                // When the price cache has gone cold, warm it back up and then try the
                // message again. This works the same way whether the message came from
                // a queue or is being executed inline through IMessageBus.InvokeAsync().
                // After 3 total attempts, the message is dead lettered if it came from a
                // queue, or the exception is thrown to the caller of InvokeAsync()
                opts.OnException<PriceCacheIsColdException>()
                    .DoThenRetry(async (runtime, lifecycle, ex) =>
                    {
                        var cache = runtime.Services.GetRequiredService<PriceCache>();
                        await cache.WarmAsync();
                    }, maxAttempts: 3);
            }).StartAsync();

        #endregion
    }

    public async ValueTask DisposeAsync()
    {
        await _host.StopAsync(CancellationToken.None);
        _host.Dispose();
    }

    [Fact]
    public async Task recovers_and_retries_a_message_from_a_queue()
    {
        var session = await _host.TrackActivity()
            .DoNotAssertOnExceptionsDetected()
            .PublishMessageAndWaitAsync(new LookUpPrice("ABC-123"));

        theCache.WarmUps.ShouldBe(1);
        theCache.LastAttempt.ShouldBe(2);

        session.MessageSucceeded.SingleMessage<LookUpPrice>().Sku.ShouldBe("ABC-123");
        session.MovedToErrorQueue.MessagesOf<LookUpPrice>().ShouldBeEmpty();
    }

    [Fact]
    public async Task recovers_and_retries_a_message_executed_inline()
    {
        // Calling RetryExecutionNowAsync() inline would throw, so this proves DoThenRetry()
        // uses InvokeResult.TryAgain for inline execution
        await _host.MessageBus().InvokeAsync(new LookUpPrice("ABC-123"), TestContext.Current.CancellationToken);

        theCache.WarmUps.ShouldBe(1);
        theCache.LastAttempt.ShouldBe(2);
    }

    [Fact]
    public async Task dead_letters_a_message_from_a_queue_once_the_attempts_are_exhausted()
    {
        theCache.CanBeWarmed = false;

        var session = await _host.TrackActivity()
            .DoNotAssertOnExceptionsDetected()
            .WaitForCondition(new WaitForDeadLetteredMessage<LookUpPrice>())
            .PublishMessageAndWaitAsync(new LookUpPrice("ABC-123"));

        // maxAttempts: 3 is the original attempt plus two "warm up, then retry" attempts
        theCache.WarmUps.ShouldBe(2);
        theCache.LastAttempt.ShouldBe(3);

        session.MovedToErrorQueue.SingleMessage<LookUpPrice>().Sku.ShouldBe("ABC-123");
        session.MessageSucceeded.MessagesOf<LookUpPrice>().ShouldBeEmpty();
    }

    [Fact]
    public async Task throws_to_the_inline_caller_once_the_attempts_are_exhausted()
    {
        theCache.CanBeWarmed = false;

        await Should.ThrowAsync<PriceCacheIsColdException>(async () =>
            await _host.MessageBus().InvokeAsync(new LookUpPrice("ABC-123"), TestContext.Current.CancellationToken));

        theCache.WarmUps.ShouldBe(2);
        theCache.LastAttempt.ShouldBe(3);
    }

    [Fact]
    public void each_retry_is_one_attempt_of_the_rule()
    {
        var options = new WolverineOptions();
        options.OnException<PriceCacheIsColdException>()
            .DoThenRetry((_, _, _) => ValueTask.CompletedTask, maxAttempts: 3, description: "Warm the price cache")
            .Then.MoveToErrorQueue();

        options.Policies.Failures.Last().ToString()
            .ShouldContain("attempt 1: Warm the price cache; attempt 2: Warm the price cache; attempt 3: Move to error queue");
    }

    [Fact]
    public void max_attempts_must_allow_at_least_one_retry()
    {
        var options = new WolverineOptions();

        Should.Throw<ArgumentOutOfRangeException>(() =>
            options.OnException<PriceCacheIsColdException>()
                .DoThenRetry((_, _, _) => ValueTask.CompletedTask, maxAttempts: 1));

        Should.Throw<ArgumentOutOfRangeException>(() =>
            options.OnException<PriceCacheIsColdException>()
                .DoThenRetry((_, _, _) => ValueTask.CompletedTask, maxAttempts: 26));
    }
}

public record LookUpPrice(string Sku);

public class PriceCacheIsColdException : Exception;

public class PriceCache
{
    public bool IsWarm { get; private set; }
    public bool CanBeWarmed { get; set; } = true;
    public int WarmUps { get; private set; }
    public int LastAttempt { get; set; }

    public Task WarmAsync()
    {
        WarmUps++;
        IsWarm = CanBeWarmed;
        return Task.CompletedTask;
    }
}

public static class PriceLookupHandler
{
    public static void Handle(LookUpPrice message, PriceCache cache, Envelope envelope)
    {
        cache.LastAttempt = envelope.Attempts;
        if (!cache.IsWarm) throw new PriceCacheIsColdException();
    }
}
