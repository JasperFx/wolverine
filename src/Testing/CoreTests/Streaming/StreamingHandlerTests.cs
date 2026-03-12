using JasperFx.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Wolverine.Runtime;
using Xunit;

namespace CoreTests.Streaming;

public class StreamingHandlerTests : IAsyncLifetime
{
    private IHost _host = null!;

    public async Task InitializeAsync()
    {
        _host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Discovery.IncludeType<StreamingHandler>();
            })
            .StartAsync();
    }

    public async Task DisposeAsync()
    {
        await _host.StopAsync();
        _host.Dispose();
    }

    [Fact]
    public async Task can_stream_messages_through_message_bus()
    {
        var bus = _host.Services.GetRequiredService<IMessageBus>();

        var results = new List<CountResponse>();

        await foreach (var response in bus.StreamAsync<CountResponse>(new CountRequest(5)))
        {
            results.Add(response);
        }

        results.Count.ShouldBe(5);
        results.Select(x => x.Number).ToArray().ShouldBe(new[] { 1, 2, 3, 4, 5 });
    }

    [Fact]
    public async Task streaming_with_no_results_returns_empty()
    {
        var bus = _host.Services.GetRequiredService<IMessageBus>();

        var results = new List<CountResponse>();

        await foreach (var response in bus.StreamAsync<CountResponse>(new CountRequest(0)))
        {
            results.Add(response);
        }

        results.Count.ShouldBe(0);
    }

    [Fact]
    public async Task streaming_respects_cancellation_token()
    {
        var bus = _host.Services.GetRequiredService<IMessageBus>();
        var cts = new CancellationTokenSource();
        var results = new List<CountResponse>();

        try
        {
            await foreach (var response in bus.StreamAsync<CountResponse>(
                new CountRequest(100), cts.Token))
            {
                results.Add(response);
                if (results.Count >= 3)
                {
                    cts.Cancel();
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Expected
        }

        results.Count.ShouldBe(3);
    }
}

public record CountRequest(int Count);
public record CountResponse(int Number);

public class StreamingHandler
{
    public async IAsyncEnumerable<CountResponse> Handle(CountRequest request)
    {
        for (int i = 1; i <= request.Count; i++)
        {
            // Simulate some async work
            await Task.Delay(10);
            yield return new CountResponse(i);
        }
    }
}
