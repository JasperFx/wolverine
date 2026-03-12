using JasperFx.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Wolverine.Runtime;
using Xunit;

namespace CoreTests.Streaming;

// Tests for streaming behavior through Executor and IMessageInvoker
public class streaming_invoker_behavior_tests : IAsyncLifetime
{
    private IHost _host = null!;

    public async Task InitializeAsync()
    {
        _host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Discovery.IncludeType<StreamReturningHandler>();
                opts.Discovery.IncludeType<EmptyStreamHandler>();
                opts.Discovery.IncludeType<ErrorThrowingStreamHandler>();
            })
            .StartAsync();
    }

    public async Task DisposeAsync()
    {
        await _host.StopAsync();
        _host.Dispose();
    }

    [Fact]
    public async Task handler_returns_async_enumerable_stream()
    {
        var bus = _host.Services.GetRequiredService<IMessageBus>();

        var results = new List<StreamResponse>();
        await foreach (var response in bus.StreamAsync<StreamResponse>(new StreamRequest(3)))
        {
            results.Add(response);
        }

        results.Count.ShouldBe(3);
        results.Select(x => x.Value).ToArray().ShouldBe(new[] { "item-1", "item-2", "item-3" });
    }

    [Fact]
    public async Task handler_returns_empty_stream()
    {
        var bus = _host.Services.GetRequiredService<IMessageBus>();

        var results = new List<EmptyResponse>();
        await foreach (var response in bus.StreamAsync<EmptyResponse>(new EmptyRequest()))
        {
            results.Add(response);
        }

        results.Count.ShouldBe(0);
    }

    [Fact]
    public async Task handler_throws_exception_after_yielding_items()
    {
        var bus = _host.Services.GetRequiredService<IMessageBus>();

        var results = new List<ErrorResponse>();

        await Should.ThrowAsync<InvalidOperationException>(async () =>
        {
            await foreach (var response in bus.StreamAsync<ErrorResponse>(new ErrorRequest()))
            {
                results.Add(response);
            }
        });

        // Should have yielded some items before the error
        results.Count.ShouldBe(2);
    }

    [Fact]
    public async Task streaming_respects_cancellation_mid_stream()
    {
        var bus = _host.Services.GetRequiredService<IMessageBus>();
        var cts = new CancellationTokenSource();
        var results = new List<StreamResponse>();

        try
        {
            await foreach (var response in bus.StreamAsync<StreamResponse>(
                new StreamRequest(100), cts.Token))
            {
                results.Add(response);
                if (results.Count >= 5)
                {
                    cts.Cancel();
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Expected
        }

        results.Count.ShouldBe(5);
    }

    [Fact]
    public async Task streaming_with_null_message_throws_argument_exception()
    {
        var bus = _host.Services.GetRequiredService<IMessageBus>();

        await Should.ThrowAsync<ArgumentNullException>(async () =>
        {
            await foreach (var response in bus.StreamAsync<StreamResponse>(null!))
            {
                // Should not reach here
            }
        });
    }

    [Fact]
    public async Task multiple_consumers_can_call_streaming_handlers()
    {
        var bus = _host.Services.GetRequiredService<IMessageBus>();

        // First consumer
        var results1 = new List<StreamResponse>();
        await foreach (var response in bus.StreamAsync<StreamResponse>(new StreamRequest(2)))
        {
            results1.Add(response);
        }

        // Second consumer immediately after
        var results2 = new List<StreamResponse>();
        await foreach (var response in bus.StreamAsync<StreamResponse>(new StreamRequest(3)))
        {
            results2.Add(response);
        }

        results1.Count.ShouldBe(2);
        results2.Count.ShouldBe(3);
    }
}

// Test fixtures
public record StreamRequest(int Count);
public record StreamResponse(string Value);

public class StreamReturningHandler
{
    public async IAsyncEnumerable<StreamResponse> Handle(StreamRequest request)
    {
        for (int i = 1; i <= request.Count; i++)
        {
            await Task.Delay(5);
            yield return new StreamResponse($"item-{i}");
        }
    }
}

public record EmptyRequest();
public record EmptyResponse();

public class EmptyStreamHandler
{
    public async IAsyncEnumerable<EmptyResponse> Handle(EmptyRequest request)
    {
        await Task.CompletedTask;
        yield break;
    }
}

public record ErrorRequest();
public record ErrorResponse(int Value);

public class ErrorThrowingStreamHandler
{
    public async IAsyncEnumerable<ErrorResponse> Handle(ErrorRequest request)
    {
        yield return new ErrorResponse(1);
        yield return new ErrorResponse(2);
        await Task.Delay(5);
        throw new InvalidOperationException("Streaming error occurred");
    }
}
