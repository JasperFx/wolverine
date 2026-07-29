using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine;
using Wolverine.Tracking;

namespace MartenTests.Saga;

public class When_ordering_a_happy_meal : PostgresqlContext, IAsyncLifetime
{
    private IHost? _host;
    private SodaRequested? _sodaRequested;

    public async ValueTask InitializeAsync()
    {
        _host = await
            Host.CreateDefaultBuilder()
                .UseWolverine(opts =>
                {
                    opts.Discovery.DisableConventionalDiscovery()
                        .IncludeType<HappyMealSaga3>()
                        .IncludeType<SodaHandler>();
                    opts.Durability.Mode = DurabilityMode.Solo;
                })
                .StartAsync();

        var session = await _host.InvokeMessageAndWaitAsync(new HappyMealOrder { Drink = "Soda" });

        _sodaRequested = session.Sent.SingleMessage<SodaRequested>();
    }

    public ValueTask DisposeAsync()
    {
        _host?.Dispose();
        return ValueTask.CompletedTask;
    }

    [Fact]
    public void should_be_complete()
    {
        _sodaRequested.ShouldNotBeNull();
    }
}