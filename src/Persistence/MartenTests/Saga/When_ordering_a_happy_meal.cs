using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine;
using Wolverine.Tracking;

namespace MartenTests.Saga;

public class When_ordering_a_happy_meal : PostgresqlContext, IAsyncLifetime
{
    private IHost? _host;
    private SodaRequested? _sodaRequested;

    public async Task InitializeAsync()
    {
        _host = await
            Host.CreateDefaultBuilder()
                .UseWolverine()
                .StartAsync();

        var session = await _host.InvokeMessageAndWaitAsync(new HappyMealOrder { Drink = "Soda" });

        _sodaRequested = session.Sent.SingleMessage<SodaRequested>();
    }

    public Task DisposeAsync()
    {
        _host?.Dispose();
        return Task.CompletedTask;
    }

    [Fact]
    public void should_be_complete()
    {
        _sodaRequested.ShouldNotBeNull();
    }
}