using Alba;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine;
using Wolverine.Tracking;

namespace SagaTests;

public class When_ordering_a_happy_meal : IAsyncLifetime
{
    private IAlbaHost? _host;
    private SodaRequested? _sodaRequested;

    public async Task InitializeAsync()
    {
        _host = await
            Host.CreateDefaultBuilder()
                .UseWolverine()
                .StartAlbaAsync();

        var session = await _host.InvokeMessageAndWaitAsync(new HappyMealOrder { Drink = "Soda" });

        _sodaRequested = session.Sent.SingleMessage<SodaRequested>();
    }

    [Fact]
    public void should_be_complete() => _sodaRequested.ShouldNotBeNull();

    public async Task DisposeAsync() => await _host.DisposeAsync();
}
