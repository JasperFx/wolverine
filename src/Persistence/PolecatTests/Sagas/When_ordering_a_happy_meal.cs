using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine;
using Wolverine.Tracking;

namespace PolecatTests.Sagas;

public class When_ordering_a_happy_meal : IAsyncLifetime
{
    private IHost? _host;
    private PcSodaRequested? _sodaRequested;

    public async Task InitializeAsync()
    {
        _host = await
            Host.CreateDefaultBuilder()
                .UseWolverine()
                .StartAsync();

        var session = await _host.InvokeMessageAndWaitAsync(new PcHappyMealOrder { Drink = "Soda" });

        _sodaRequested = session.Sent.SingleMessage<PcSodaRequested>();
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
