using System.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using Wolverine.Kafka.Internals;
using Wolverine.Runtime;
using Wolverine.Transports;

namespace Wolverine.Kafka.Tests;

// GH-5236. KafkaListener.DeferAsync() offered the envelope back to the receiver inline. A durable receiver whose
// inbox write keeps failing defers from inside its own receive, so receive -> fail -> defer -> receive recursed
// until the process died of a stack overflow. These run without a broker: the receiver below defers every
// envelope it is given, which is what the failing inbox does.
public class deferred_redelivery_does_not_recurse_5236 : IDisposable
{
    private readonly TimeSpan _originalDelay = KafkaDeferredRedelivery.Delay;
    private readonly CancellationTokenSource _stopping = new();
    private readonly AlwaysDefersReceiver _receiver;

    public deferred_redelivery_does_not_recurse_5236()
    {
        KafkaDeferredRedelivery.Delay = TimeSpan.FromMilliseconds(10);
        _receiver = new AlwaysDefersReceiver(_stopping.Token);
    }

    public void Dispose()
    {
        KafkaDeferredRedelivery.Delay = _originalDelay;
        _stopping.Cancel();
        _stopping.Dispose();
    }

    [Fact]
    public async Task an_envelope_that_keeps_being_deferred_does_not_grow_the_stack()
    {
        await _receiver.ReceivedAsync(_receiver.Listener, new Envelope());

        await _receiver.WaitForReceivesAsync(20);

        // Every redelivery starts on a fresh task, so the stack is no deeper on the 20th receive than on the second
        int[] depths;
        lock (_receiver.Depths) depths = _receiver.Depths.ToArray();
        depths.Skip(1).Max().ShouldBeLessThanOrEqualTo(depths[1] + 5);
    }

    [Fact]
    public async Task defer_returns_without_waiting_for_the_redelivery()
    {
        var envelope = new Envelope();

        var defer = KafkaDeferredRedelivery.DeferAsync(_receiver.Listener, _receiver, envelope, _stopping.Token,
            NullLogger.Instance);

        defer.IsCompleted.ShouldBeTrue();
        await _receiver.WaitForReceivesAsync(1);
    }

    [Fact]
    public async Task nothing_is_redelivered_once_the_listener_stops()
    {
        await _receiver.ReceivedAsync(_receiver.Listener, new Envelope());
        await _receiver.WaitForReceivesAsync(3);

        await _stopping.CancelAsync();
        await Task.Delay(100, TestContext.Current.CancellationToken);
        var receives = _receiver.Receives;

        await Task.Delay(200, TestContext.Current.CancellationToken);
        _receiver.Receives.ShouldBe(receives);
    }

    public class AlwaysDefersReceiver : IReceiver
    {
        private readonly CancellationToken _stopping;
        private int _receives;

        public AlwaysDefersReceiver(CancellationToken stopping)
        {
            _stopping = stopping;
            Listener = Substitute.For<IListener>();
            Listener.Address.Returns(new Uri("kafka://topic/deferred"));
        }

        public IListener Listener { get; }

        public List<int> Depths { get; } = new();

        public int Receives => Volatile.Read(ref _receives);

        public async ValueTask ReceivedAsync(IListener listener, Envelope envelope)
        {
            lock (Depths) Depths.Add(new StackTrace().FrameCount);
            Interlocked.Increment(ref _receives);

            // The failed inbox write: settle by deferring, from inside this receive
            await KafkaDeferredRedelivery.DeferAsync(listener, this, envelope, _stopping, NullLogger.Instance);
        }

        public async Task WaitForReceivesAsync(int count)
        {
            var deadline = DateTimeOffset.UtcNow.AddSeconds(10);
            while (Receives < count)
            {
                DateTimeOffset.UtcNow.ShouldBeLessThan(deadline, "timed out waiting for redelivery");
                await Task.Delay(10, TestContext.Current.CancellationToken);
            }
        }

        public ValueTask ReceivedAsync(IListener listener, Envelope[] messages) => ValueTask.CompletedTask;

        public ValueTask DrainAsync() => ValueTask.CompletedTask;

        public IHandlerPipeline Pipeline => null!;

        public void Dispose()
        {
        }
    }
}
