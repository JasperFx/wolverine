using System.Diagnostics;
using Dekaf.Consumer;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using Wolverine.Kafka.Internals;
using Wolverine.Transports;

namespace Wolverine.Kafka.Tests;

/// <summary>
/// GH-4422. <c>KafkaListener.StopAsync</c> bounded the receive-loop drain (GH-3434) but not the offset flush and
/// consumer close that follow it. Closing a consumer leaves the group, and against a degraded broker or
/// coordinator that wait never ended, so <c>IHost.StopAsync</c> never completed -- past both
/// <c>DrainTimeout</c> and <c>HostOptions.ShutdownTimeout</c>.
///
/// <para>
/// No broker here: a substituted <see cref="IKafkaConsumer{TKey,TValue}" /> stands in for the wedge, which is the
/// only way to reproduce "never returns" deterministically. Every test bounds its own wait, so a regression fails
/// these tests instead of hanging the suite forever.
/// </para>
/// </summary>
public class bounded_consumer_close_4422 : IDisposable
{
    // Deliberately short so the test asserts the BOUND rather than waiting out the real 30s default.
    private static readonly TimeSpan TheDrainTimeout = TimeSpan.FromMilliseconds(250);

    private readonly ManualResetEventSlim _consuming = new(false);
    private readonly ManualResetEventSlim _closeEntered = new(false);
    private readonly TaskCompletionSource _releaseClose = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public void Dispose()
    {
        // Never leave an abandoned close parked on the gate after the test.
        _releaseClose.TrySetResult();
        _consuming.Dispose();
        _closeEntered.Dispose();
    }

    private IKafkaConsumer<string, byte[]> buildConsumer()
    {
        var consumer = Substitute.For<IKafkaConsumer<string, byte[]>>();

        // The real ConsumeOneAsync waits for a record or the token. Mirroring that keeps the consume loop parked
        // exactly where it sits in production.
        consumer.ConsumeOneAsync(Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(ci => waitForCancellationAsync(ci.ArgAt<CancellationToken>(1)));

        return consumer;
    }

    private async ValueTask<ConsumeResult<string, byte[]>?> waitForCancellationAsync(CancellationToken token)
    {
        _consuming.Set();
        await Task.Delay(Timeout.Infinite, token);
        return null;
    }

    private void makeCloseWedge(IKafkaConsumer<string, byte[]> consumer)
    {
        consumer.CloseAsync(Arg.Any<CancellationToken>()).Returns(_ =>
        {
            _closeEntered.Set();
            // Stands in for a group leave that never completes.
            return new ValueTask(_releaseClose.Task);
        });
    }

    private KafkaListener startListener(IKafkaConsumer<string, byte[]> consumer)
    {
        var transport = new KafkaTransport();
        var topic = transport.Topics["shutdown-4422"];
        var config = new KafkaConsumerOptions { GroupId = "shutdown-4422" };

        var listener = new KafkaListener(topic, config, consumer, [topic.TopicName], Substitute.For<IReceiver>(),
            NullLogger<KafkaListener>.Instance, TheDrainTimeout);

        // A consumer that never initialized has nothing to close, so let the loop get past initialization first
        _consuming.Wait(TimeSpan.FromSeconds(5)).ShouldBeTrue("the consume loop should have started");

        return listener;
    }

    [Fact]
    public async Task stop_async_is_bounded_when_close_never_returns()
    {
        var consumer = buildConsumer();
        makeCloseWedge(consumer);

        var listener = startListener(consumer);

        var stopwatch = Stopwatch.StartNew();

        // WaitAsync rather than a bare await: without the fix this never completes, and a test that
        // FAILS on a timeout is worth vastly more than one that hangs the whole suite.
        await listener.StopAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

        stopwatch.Stop();

        _closeEntered.Wait(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken)
            .ShouldBeTrue("CloseAsync() should still be attempted — the fix bounds the wait, it does not skip the close");

        // The budget covers the loop drain and then the flush+close, so a couple of multiples of the
        // drain timeout is the honest ceiling. The point is that it is finite at all.
        stopwatch.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task an_abandoned_close_does_not_get_its_consumer_disposed()
    {
        var consumer = buildConsumer();
        makeCloseWedge(consumer);

        var listener = startListener(consumer);

        await listener.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

        _closeEntered.Wait(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken).ShouldBeTrue();

        // The close is still in flight, so teardown leaves the consumer alone rather than disposing it underneath
        await consumer.DidNotReceive().DisposeAsync();
    }

    [Fact]
    public async Task a_healthy_consumer_is_still_closed_and_disposed()
    {
        // No wedge: the ordinary path must be completely unchanged.
        var consumer = buildConsumer();

        var listener = startListener(consumer);

        await listener.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

        await consumer.Received().CloseAsync(Arg.Any<CancellationToken>());
        await consumer.Received().DisposeAsync();
    }
}
