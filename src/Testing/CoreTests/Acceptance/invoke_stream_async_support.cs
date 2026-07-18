using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using System.Runtime.CompilerServices;
using Wolverine.Tracking;
using Xunit;

namespace CoreTests.Acceptance;

// ---------------------------------------------------------------------------
// Message types
// ---------------------------------------------------------------------------

public record NumberToSum(int Value);

public record NumberSum(int Total, int Count);

// Separate element type routed to a handler without a CancellationToken parameter.
public record PlainNumber(int Value);

// Separate element type so the cascading test routes to a dedicated handler that
// returns a (response, cascading message) tuple.
public record CascadingNumber(int Value);

public record StreamIngestionCompleted(int Count);

// Separate element type routed to a handler that throws mid-drain.
public record FaultingNumber(int Value);

// Deliberately has NO stream handler — used to assert the clear error message.
public record UnhandledNumber(int Value);

// ---------------------------------------------------------------------------
// Handlers
// ---------------------------------------------------------------------------

public static class NumberStreamHandler
{
    public static async Task<NumberSum> Handle(IAsyncEnumerable<NumberToSum> numbers,
        CancellationToken cancellationToken)
    {
        var total = 0;
        var count = 0;
        await foreach (var number in numbers.WithCancellation(cancellationToken))
        {
            total += number.Value;
            count++;
        }

        return new NumberSum(total, count);
    }
}

public static class PlainNumberStreamHandler
{
    public static async Task<NumberSum> Handle(IAsyncEnumerable<PlainNumber> numbers)
    {
        var total = 0;
        var count = 0;
        await foreach (var number in numbers)
        {
            total += number.Value;
            count++;
        }

        return new NumberSum(total, count);
    }
}

public static class CascadingNumberStreamHandler
{
    public static async Task<(NumberSum, StreamIngestionCompleted)> Handle(
        IAsyncEnumerable<CascadingNumber> numbers)
    {
        var total = 0;
        var count = 0;
        await foreach (var number in numbers)
        {
            total += number.Value;
            count++;
        }

        return (new NumberSum(total, count), new StreamIngestionCompleted(count));
    }
}

public static class StreamIngestionCompletedHandler
{
    public static void Handle(StreamIngestionCompleted completed, StreamCompletionTracker tracker)
    {
        tracker.Add(completed);
    }
}

public class StreamCompletionTracker
{
    private readonly List<StreamIngestionCompleted> _completions = new();
    public IReadOnlyList<StreamIngestionCompleted> Completions => _completions;
    public void Add(StreamIngestionCompleted completed) => _completions.Add(completed);
}

public static class FaultingNumberStreamHandler
{
    public static async Task<NumberSum> Handle(IAsyncEnumerable<FaultingNumber> numbers)
    {
        var count = 0;
        await foreach (var _ in numbers)
        {
            count++;
            if (count >= 2)
            {
                throw new InvalidOperationException("stream handler faulted mid-drain");
            }
        }

        return new NumberSum(0, count);
    }
}

// ---------------------------------------------------------------------------
// Tests
// ---------------------------------------------------------------------------

public class invoke_stream_async_support
{
    private static async IAsyncEnumerable<T> toStream<T>(IEnumerable<T> items)
    {
        foreach (var item in items)
        {
            yield return item;
            await Task.Yield();
        }
    }

    [Fact]
    public async Task invoke_stream_returns_aggregated_response()
    {
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine()
            .StartAsync();

        var bus = host.MessageBus();

        var numbers = toStream(Enumerable.Range(1, 4).Select(i => new NumberToSum(i)));
        var sum = await bus.InvokeStreamAsync<NumberToSum, NumberSum>(numbers);

        sum.Total.ShouldBe(10);
        sum.Count.ShouldBe(4);
    }

    [Fact]
    public async Task empty_stream_still_returns_response()
    {
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine()
            .StartAsync();

        var bus = host.MessageBus();

        var sum = await bus.InvokeStreamAsync<NumberToSum, NumberSum>(toStream(Array.Empty<NumberToSum>()));

        sum.Total.ShouldBe(0);
        sum.Count.ShouldBe(0);
    }

    [Fact]
    public async Task handler_without_cancellation_token_parameter_works()
    {
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine()
            .StartAsync();

        var bus = host.MessageBus();

        var numbers = toStream([new PlainNumber(5), new PlainNumber(7)]);
        var sum = await bus.InvokeStreamAsync<PlainNumber, NumberSum>(numbers);

        sum.Total.ShouldBe(12);
        sum.Count.ShouldBe(2);
    }

    [Fact]
    public async Task no_stream_handler_throws_clear_not_supported()
    {
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine()
            .StartAsync();

        var bus = host.MessageBus();

        var ex = await Should.ThrowAsync<NotSupportedException>(async () =>
        {
            await bus.InvokeStreamAsync<UnhandledNumber, NumberSum>(toStream([new UnhandledNumber(1)]));
        });

        ex.Message.ShouldContain(nameof(UnhandledNumber));
        ex.Message.ShouldContain("IAsyncEnumerable");
    }

    [Fact]
    public async Task handler_exception_mid_drain_surfaces_to_caller()
    {
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine()
            .StartAsync();

        var bus = host.MessageBus();

        var numbers = toStream(Enumerable.Range(0, 10).Select(i => new FaultingNumber(i)));
        var ex = await Should.ThrowAsync<InvalidOperationException>(async () =>
        {
            await bus.InvokeStreamAsync<FaultingNumber, NumberSum>(numbers);
        });

        ex.Message.ShouldBe("stream handler faulted mid-drain");
    }

    [Fact]
    public async Task cancellation_propagates_into_the_handler()
    {
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine()
            .StartAsync();

        var bus = host.MessageBus();

        using var cts = new CancellationTokenSource();

        async IAsyncEnumerable<NumberToSum> infinite(
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var i = 0;
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return new NumberToSum(i++);
                if (i >= 2)
                {
                    await cts.CancelAsync();
                }

                await Task.Yield();
            }
            // ReSharper disable once IteratorNeverReturns
        }

        await Should.ThrowAsync<OperationCanceledException>(async () =>
        {
            await bus.InvokeStreamAsync<NumberToSum, NumberSum>(infinite(), cts.Token);
        });
    }

    [Fact]
    public async Task cascading_messages_from_stream_handler_are_published()
    {
        var tracker = new StreamCompletionTracker();

        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Services.AddSingleton(tracker);
            })
            .StartAsync();

        NumberSum? sum = null;
        await host.ExecuteAndWaitAsync(async context =>
        {
            var numbers = toStream([new CascadingNumber(2), new CascadingNumber(3)]);
            sum = await context.InvokeStreamAsync<CascadingNumber, NumberSum>(numbers);
        });

        sum.ShouldNotBeNull();
        sum.Total.ShouldBe(5);
        sum.Count.ShouldBe(2);

        tracker.Completions.Count.ShouldBe(1);
        tracker.Completions[0].Count.ShouldBe(2);
    }

    [Fact]
    public async Task invoke_stream_with_delivery_options()
    {
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine()
            .StartAsync();

        var bus = host.MessageBus();
        var options = new DeliveryOptions();

        var numbers = toStream([new NumberToSum(1), new NumberToSum(2)]);
        var sum = await bus.InvokeStreamAsync<NumberToSum, NumberSum>(numbers, options);

        sum.Total.ShouldBe(3);
    }

    [Fact]
    public async Task ordinary_single_message_invoke_is_unaffected_by_stream_chains()
    {
        // The IAsyncEnumerable<NumberToSum> chain registered in this assembly must not
        // interfere with normal single-message request/reply on unrelated types.
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine()
            .StartAsync();

        var bus = host.MessageBus();

        var items = new List<StreamItem>();
        await foreach (var item in bus.StreamAsync<StreamItem>(new StreamRequest(2)))
        {
            items.Add(item);
        }

        items.Count.ShouldBe(2);
    }
}
