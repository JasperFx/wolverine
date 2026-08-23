using Amazon.SQS.Model;
using JasperFx.Core;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Wolverine.AmazonSqs.Internal;
using Wolverine.Configuration;

namespace Wolverine.AmazonSqs.Tests.Internal;

/// <summary>
/// GH-4019. The scheduling half of the inline visibility heartbeat, with the SQS call stubbed out.
/// The LocalStack half lives in inline_visibility_heartbeat_4019.
/// </summary>
public class sqs_visibility_heartbeat_4019 : IAsyncDisposable
{
    private readonly List<Message[]> _extensions = new();
    private readonly List<Message> _reject = new();
    private readonly Uri _uri = new("sqs://heartbeat");
    private SqsVisibilityHeartbeat? _heartbeat;

    private SqsVisibilityHeartbeat start(TimeSpan? visibility = null, TimeSpan? maximum = null, TimeSpan? interval = null)
    {
        _heartbeat = new SqsVisibilityHeartbeat(
            visibility ?? 10.Seconds(),
            maximum ?? 12.Hours(),
            (messages, _) =>
            {
                lock (_extensions)
                {
                    _extensions.Add(messages);
                }

                IReadOnlyList<Message> rejected = messages.Where(m => _reject.Contains(m)).ToList();
                return Task.FromResult(rejected);
            },
            _uri, NullLogger.Instance, CancellationToken.None, interval ?? 50.Milliseconds());

        return _heartbeat;
    }

    private static Message message(string handle)
    {
        return new Message { MessageId = handle, ReceiptHandle = handle };
    }

    private int extensionCount()
    {
        lock (_extensions)
        {
            return _extensions.Count;
        }
    }

    private Message[] lastExtension()
    {
        lock (_extensions)
        {
            return _extensions.Last();
        }
    }

    private static async Task waitUntil(Func<bool> condition, TimeSpan? timeout = null)
    {
        var deadline = DateTimeOffset.UtcNow.Add(timeout ?? 5.Seconds());
        while (!condition())
        {
            if (DateTimeOffset.UtcNow > deadline)
            {
                throw new TimeoutException("Condition not met in time");
            }

            await Task.Delay(10, TestContext.Current.CancellationToken);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_heartbeat != null)
        {
            await _heartbeat.DisposeAsync();
        }
    }

    [Fact]
    public void default_interval_is_half_the_visibility_timeout()
    {
        var heartbeat = new SqsVisibilityHeartbeat(120.Seconds(), 12.Hours(),
            (_, _) => Task.FromResult<IReadOnlyList<Message>>(Array.Empty<Message>()), _uri,
            NullLogger.Instance, CancellationToken.None);

        heartbeat.Interval.ShouldBe(60.Seconds());
        _heartbeat = heartbeat;
    }

    [Fact]
    public void default_interval_never_drops_under_one_second()
    {
        var heartbeat = new SqsVisibilityHeartbeat(1.Seconds(), 12.Hours(),
            (_, _) => Task.FromResult<IReadOnlyList<Message>>(Array.Empty<Message>()), _uri,
            NullLogger.Instance, CancellationToken.None);

        heartbeat.Interval.ShouldBe(1.Seconds());
        _heartbeat = heartbeat;
    }

    [Fact]
    public async Task nothing_is_extended_when_nothing_is_in_flight()
    {
        start();

        await Task.Delay(300, TestContext.Current.CancellationToken);

        extensionCount().ShouldBe(0);
    }

    [Fact]
    public async Task tracked_messages_are_extended_on_every_tick_until_settled()
    {
        var heartbeat = start();
        var one = message("one");
        var two = message("two");

        heartbeat.Track([one, two]);
        heartbeat.InFlightCount.ShouldBe(2);

        await waitUntil(() => extensionCount() >= 2);
        lastExtension().Select(x => x.ReceiptHandle).OrderBy(x => x).ShouldBe(["one", "two"]);

        heartbeat.Settled(one);
        heartbeat.InFlightCount.ShouldBe(1);

        // Give the loop a couple of ticks to see the change, then every extension is "two" only
        await Task.Delay(150, TestContext.Current.CancellationToken);
        var before = extensionCount();
        await waitUntil(() => extensionCount() > before);
        lastExtension().Select(x => x.ReceiptHandle).ShouldBe(["two"]);
    }

    [Fact]
    public async Task untrack_stops_every_extension()
    {
        var heartbeat = start();
        var messages = new[] { message("a"), message("b"), message("c") };

        heartbeat.Track(messages);
        await waitUntil(() => extensionCount() >= 1);

        heartbeat.Untrack(messages);
        heartbeat.InFlightCount.ShouldBe(0);

        // Let any in-progress tick finish, then nothing else lands
        await Task.Delay(150, TestContext.Current.CancellationToken);
        var count = extensionCount();
        await Task.Delay(200, TestContext.Current.CancellationToken);
        extensionCount().ShouldBe(count);
    }

    [Fact]
    public async Task a_message_sqs_refuses_to_extend_is_dropped()
    {
        var heartbeat = start();
        var good = message("good");
        var stale = message("stale");
        _reject.Add(stale);

        heartbeat.Track([good, stale]);

        await waitUntil(() => heartbeat.InFlightCount == 1);

        // Only the live one is still being kept alive
        await Task.Delay(150, TestContext.Current.CancellationToken);
        var before = extensionCount();
        await waitUntil(() => extensionCount() > before);
        lastExtension().Select(x => x.ReceiptHandle).ShouldBe(["good"]);
    }

    [Fact]
    public async Task a_message_is_not_extended_past_the_maximum()
    {
        // Visibility 200ms, maximum 500ms: the first tick or two extend it, then an extension would carry
        // it past the maximum and it is dropped instead
        var heartbeat = start(visibility: 200.Milliseconds(), maximum: 500.Milliseconds(), interval: 50.Milliseconds());
        var message = sqs_visibility_heartbeat_4019.message("long-runner");

        heartbeat.Track([message]);

        await waitUntil(() => extensionCount() >= 1);
        await waitUntil(() => heartbeat.InFlightCount == 0, 3.Seconds());

        // And it stays dropped
        var count = extensionCount();
        await Task.Delay(200, TestContext.Current.CancellationToken);
        extensionCount().ShouldBe(count);
    }

    [Fact]
    public async Task tick_is_a_no_op_when_the_extension_throws_and_the_loop_keeps_going()
    {
        var calls = 0;
        var heartbeat = new SqsVisibilityHeartbeat(10.Seconds(), 12.Hours(), (_, _) =>
        {
            Interlocked.Increment(ref calls);
            throw new InvalidOperationException("SQS is having a moment");
        }, _uri, NullLogger.Instance, CancellationToken.None, 50.Milliseconds());
        _heartbeat = heartbeat;

        heartbeat.Track([message("x")]);

        await waitUntil(() => Volatile.Read(ref calls) >= 3);
        heartbeat.InFlightCount.ShouldBe(1);
    }

    [Fact]
    public void maximum_visibility_extension_is_bounded_by_the_sqs_limit()
    {
        var queue = new AmazonSqsTransport().Queues["inline"];

        queue.MaximumVisibilityExtension = 1.Hours();
        queue.MaximumVisibilityExtension.ShouldBe(1.Hours());

        Should.Throw<ArgumentOutOfRangeException>(() => queue.MaximumVisibilityExtension = 13.Hours());
        Should.Throw<ArgumentOutOfRangeException>(() => queue.MaximumVisibilityExtension = TimeSpan.Zero);
    }

    [Fact]
    public void extend_visibility_while_handling_is_off_by_default_and_configurable()
    {
        var transport = new AmazonSqsTransport();
        var queue = transport.Queues["inline"];
        queue.ExtendVisibilityWhileHandling.ShouldBeFalse();
        queue.MaximumVisibilityExtension.ShouldBe(AmazonSqsQueue.MaximumSqsVisibilityExtension);

        var configuration = new AmazonSqsListenerConfiguration(queue);
        configuration.ProcessInline().ExtendVisibilityWhileHandling(2.Hours());
        ((IDelayedEndpointConfiguration)configuration).Apply();

        queue.Mode.ShouldBe(EndpointMode.Inline);
        queue.ExtendVisibilityWhileHandling.ShouldBeTrue();
        queue.MaximumVisibilityExtension.ShouldBe(2.Hours());
    }
}
