using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine.Tracking;
using Xunit;

namespace CoreTests.Acceptance;

// ---------------------------------------------------------------------------
// Message types
// ---------------------------------------------------------------------------

public record StreamRequest(int Count);

public record StreamItem(int Value);

// A typed IAsyncEnumerable<T> that should cascade as individual messages
// (latent bug fix verification)
public record CascadeRequest(int Count);

public record CascadeItem(int Value);

// Separate request type so we can route to a dedicated handler that throws after
// yielding a fixed number of items (mid-stream fault behavior).
public record FaultingStreamRequest(int YieldBeforeThrow);

// ---------------------------------------------------------------------------
// Handlers
// ---------------------------------------------------------------------------

public static class StreamingRequestHandler
{
    public static async IAsyncEnumerable<StreamItem> Handle(StreamRequest request,
        CancellationToken cancellationToken)
    {
        for (var i = 0; i < request.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return new StreamItem(i);
            await Task.Yield();
        }
    }
}

// Returns a typed IAsyncEnumerable<T> (T != object) — should cascade each item
// when called via InvokeAsync (not StreamAsync). This verifies the latent bug fix.
public static class CascadeStreamingHandler
{
    public static async IAsyncEnumerable<CascadeItem> Handle(CascadeRequest request)
    {
        for (var i = 0; i < request.Count; i++)
        {
            yield return new CascadeItem(i);
            await Task.Yield();
        }
    }
}

public static class CascadeItemHandler
{
    public static void Handle(CascadeItem item, CascadeItemTracker tracker)
    {
        tracker.Add(item);
    }
}

public class CascadeItemTracker
{
    private readonly List<CascadeItem> _items = new();
    public IReadOnlyList<CascadeItem> Items => _items;
    public void Add(CascadeItem item) => _items.Add(item);
}

public static class FaultingStreamingHandler
{
    public static async IAsyncEnumerable<StreamItem> Handle(FaultingStreamRequest request)
    {
        for (var i = 0; i < request.YieldBeforeThrow; i++)
        {
            yield return new StreamItem(i);
            await Task.Yield();
        }

        throw new InvalidOperationException("handler faulted mid-stream");
    }
}

// ---------------------------------------------------------------------------
// Tests
// ---------------------------------------------------------------------------

public class streaming_handler_support
{
    [Fact]
    public async Task stream_items_from_local_handler()
    {
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine()
            .StartAsync();

        var bus = host.Services.GetRequiredService<IMessageBus>();

        var items = new List<StreamItem>();
        await foreach (var item in bus.StreamAsync<StreamItem>(new StreamRequest(3)))
        {
            items.Add(item);
        }

        items.Count.ShouldBe(3);
        items.Select(i => i.Value).ShouldBe([0, 1, 2]);
    }

    [Fact]
    public async Task stream_returns_empty_when_handler_yields_nothing()
    {
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine()
            .StartAsync();

        var bus = host.Services.GetRequiredService<IMessageBus>();

        var items = new List<StreamItem>();
        await foreach (var item in bus.StreamAsync<StreamItem>(new StreamRequest(0)))
        {
            items.Add(item);
        }

        items.ShouldBeEmpty();
    }

    [Fact]
    public async Task cancellation_stops_iteration()
    {
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine()
            .StartAsync();

        var bus = host.Services.GetRequiredService<IMessageBus>();

        using var cts = new CancellationTokenSource();
        var count = 0;

        await Should.ThrowAsync<OperationCanceledException>(async () =>
        {
            await foreach (var item in bus.StreamAsync<StreamItem>(new StreamRequest(100), cts.Token))
            {
                count++;
                if (count >= 2)
                {
                    cts.Cancel();
                }
            }
        });

        count.ShouldBe(2);
    }

    [Fact]
    public async Task typed_async_enumerable_cascades_items_via_regular_invoke()
    {
        // Verifies the latent bug fix: IAsyncEnumerable<T> (T != object) returned from a handler
        // should iterate and cascade each item when called via InvokeAsync (not StreamAsync).
        var tracker = new CascadeItemTracker();

        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Services.AddSingleton(tracker);
            })
            .StartAsync();

        await host.InvokeMessageAndWaitAsync(new CascadeRequest(3));

        tracker.Items.Count.ShouldBe(3);
        // Sort before asserting — cascaded messages are dispatched concurrently so arrival order is non-deterministic.
        tracker.Items.Select(i => i.Value).OrderBy(v => v).ShouldBe([0, 1, 2]);
    }

    [Fact]
    public async Task handler_exception_after_partial_yield_surfaces_to_caller_with_items_already_consumed()
    {
        // Caller should observe the items yielded before the throw, then the exception when
        // the enumerator advances past them. This is the contract end-users will rely on when
        // composing streaming handlers — partial results are not silently swallowed.
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine()
            .StartAsync();

        var bus = host.Services.GetRequiredService<IMessageBus>();

        var items = new List<StreamItem>();
        var ex = await Should.ThrowAsync<InvalidOperationException>(async () =>
        {
            await foreach (var item in bus.StreamAsync<StreamItem>(new FaultingStreamRequest(2)))
            {
                items.Add(item);
            }
        });

        ex.Message.ShouldBe("handler faulted mid-stream");
        items.Select(i => i.Value).ShouldBe([0, 1]);
    }

    [Fact]
    public async Task stream_with_delivery_options()
    {
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine()
            .StartAsync();

        var bus = host.Services.GetRequiredService<IMessageBus>();
        var options = new DeliveryOptions();

        var items = new List<StreamItem>();
        await foreach (var item in bus.StreamAsync<StreamItem>(new StreamRequest(2), options))
        {
            items.Add(item);
        }

        items.Count.ShouldBe(2);
    }
}
