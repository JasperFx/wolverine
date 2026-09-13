using System.Diagnostics;
using Confluent.Kafka;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using Wolverine.Kafka.Internals;
using Wolverine.Transports;

namespace Wolverine.Kafka.Tests;

/// <summary>
/// GH-4422. <c>KafkaListener.StopAsync</c> bounded the receive-loop drain (GH-3434) and then called
/// <c>_consumer.Close()</c> with no timeout and no token. That call is a synchronous P/Invoke into
/// <c>rd_kafka_consumer_close</c>, which waits on an infinite queue pop for the consumer group to
/// finish revoke/commit/leave; against a degraded broker or coordinator it never returns
/// (librdkafka#4519), so <c>IHost.StopAsync</c> never completed — observed still wedged 20+ minutes
/// later, past both <c>DrainTimeout</c> and <c>HostOptions.ShutdownTimeout</c>.
///
/// <para>
/// No broker here: a substituted <see cref="IConsumer{TKey,TValue}" /> stands in for the wedge, which
/// is the only way to reproduce "never returns" deterministically. Every test bounds its own wait, so
/// a regression fails these tests instead of hanging the suite forever.
/// </para>
/// </summary>
public class bounded_consumer_close_4422 : IDisposable
{
    // Deliberately short so the test asserts the BOUND rather than waiting out the real 30s default.
    private static readonly TimeSpan TheDrainTimeout = TimeSpan.FromMilliseconds(250);

    private readonly ManualResetEventSlim _closeEntered = new(false);
    private readonly ManualResetEventSlim _releaseClose = new(false);

    public void Dispose()
    {
        // Never leave an abandoned teardown thread parked on the gate after the test.
        _releaseClose.Set();
        _closeEntered.Dispose();
        _releaseClose.Dispose();
    }

    private IConsumer<string, byte[]> buildConsumer()
    {
        var consumer = Substitute.For<IConsumer<string, byte[]>>();

        // The real Consume(token) blocks until a record arrives or the token trips. Mirroring that
        // keeps the consume loop parked exactly where it sits in production.
        consumer.Consume(Arg.Any<CancellationToken>()).Returns(ci =>
        {
            var token = ci.Arg<CancellationToken>();
            token.WaitHandle.WaitOne();
            throw new OperationCanceledException(token);
        });

        return consumer;
    }

    private void makeCloseWedge(IConsumer<string, byte[]> consumer)
    {
        consumer.When(x => x.Close()).Do(_ =>
        {
            _closeEntered.Set();
            // Stands in for rd_kafka_consumer_close never returning.
            _releaseClose.Wait();
        });
    }

    private static KafkaListener listenerFor(IConsumer<string, byte[]> consumer)
    {
        var transport = new KafkaTransport();
        var topic = transport.Topics["shutdown-4422"];
        var config = new ConsumerConfig { GroupId = "shutdown-4422" };

        return new KafkaListener(topic, config, consumer, Substitute.For<IReceiver>(),
            NullLogger<KafkaListener>.Instance, TheDrainTimeout);
    }

    [Fact]
    public async Task stop_async_is_bounded_when_close_never_returns()
    {
        var consumer = buildConsumer();
        makeCloseWedge(consumer);

        var listener = listenerFor(consumer);

        var stopwatch = Stopwatch.StartNew();

        // WaitAsync rather than a bare await: without the fix this never completes, and a test that
        // FAILS on a timeout is worth vastly more than one that hangs the whole suite.
        await listener.StopAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

        stopwatch.Stop();

        _closeEntered.Wait(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken)
            .ShouldBeTrue("Close() should still be attempted — the fix bounds the wait, it does not skip the close");

        // The budget covers the loop drain and then the flush+close, so a couple of multiples of the
        // drain timeout is the honest ceiling. The point is that it is finite at all.
        stopwatch.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task an_abandoned_close_does_not_get_its_consumer_disposed()
    {
        var consumer = buildConsumer();
        makeCloseWedge(consumer);

        var listener = listenerFor(consumer);

        await listener.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

        _closeEntered.Wait(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken).ShouldBeTrue();

        // The abandoned thread is still inside the native Close(), and it owns the handle. Destroying
        // it underneath that thread would trade a hang for a crash, so teardown leaves it to process
        // exit instead.
        consumer.DidNotReceive().Dispose();
    }

    [Fact]
    public async Task a_healthy_consumer_is_still_closed_and_disposed()
    {
        // No wedge: the ordinary path must be completely unchanged.
        var consumer = buildConsumer();

        var listener = listenerFor(consumer);

        await listener.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

        consumer.Received().Close();
        consumer.Received().Dispose();
    }
}
