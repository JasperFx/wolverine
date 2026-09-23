using JasperFx.Core;
using Shouldly;
using Wolverine.Configuration;
using Wolverine.RabbitMQ.Internal;
using Xunit;

namespace Wolverine.RabbitMQ.Tests.ConventionalRouting;

public class when_discovering_a_listening_endpoint_with_all_defaults : ConventionalRoutingContext, IAsyncLifetime
{
    private RabbitMqEndpoint theEndpoint = null!;
    private readonly Uri theExpectedUri = "rabbitmq://queue/routed".ToUri();

    public async ValueTask InitializeAsync()
    {
        await ConfigureConventions(x=> x.IncludeTypes(ConventionalRoutingTestDefaults.RoutingMessageOnly));
        theEndpoint = (await theRuntime()).Endpoints.EndpointFor(theExpectedUri).ShouldBeOfType<RabbitMqQueue>();
    }

    // GH-3965: shadows ConventionalRoutingContext.DisposeAsync -- must stop the host itself.
    ValueTask IAsyncDisposable.DisposeAsync() => DisposeHostAsync();

    [Fact]
    public void endpoint_should_be_a_listener()
    {
        theEndpoint.IsListener.ShouldBeTrue();
    }

    [Fact]
    public void endpoint_should_not_be_null()
    {
        theEndpoint.ShouldNotBeNull();
    }

    [Fact]
    public void mode_is_inline_by_default()
    {
        theEndpoint.Mode.ShouldBe(EndpointMode.Inline);
    }

    [Fact]
    public async Task should_be_an_active_listener()
    {
        (await theRuntime()).Endpoints.ActiveListeners().Any(x => x.Uri == theExpectedUri)
            .ShouldBeTrue();
    }

    [Fact]
    public async Task the_queue_was_declared()
    {
        var transport = await theTransport();
        transport.Queues.Contains("routed").ShouldBeTrue();

        // HasDeclared is set only after QueueDeclareAsync returns, so unlike the QueueType/Arguments
        // assertions GH-4559 dealt with, this one cannot be true against a dead broker
        transport.Queues["routed"].HasDeclared.ShouldBeTrue();

        using var probe = await RabbitManagementProbe.RequireAsync(TestContext.Current.CancellationToken);
        (await probe.GetQueueTypeAsync("routed", token: TestContext.Current.CancellationToken))
            .ShouldNotBeNull("the broker has no queue named 'routed'");
    }

    /// <summary>
    /// GH-4559. Bindings had no coverage in either conventional-routing discovery fixture: the one test
    /// that would have provided it sat commented out in
    /// <see cref="when_discovering_a_sender_with_all_defaults"/>, where <c>DisableListenerDiscovery</c>
    /// means there is no queue to bind in the first place. Here listener discovery is on, so the
    /// convention really does bind the queue to the exchange of the same name.
    /// </summary>
    [Fact]
    public async Task the_queue_is_bound_to_the_exchange_of_the_same_name()
    {
        var transport = await theTransport();
        var queue = transport.Queues["routed"];

        var binding = queue.Bindings().Single();
        binding.ExchangeName.ShouldBe("routed");
        binding.HasDeclared.ShouldBeTrue();

        // and the broker's own binding list agrees
        using var probe = await RabbitManagementProbe.RequireAsync(TestContext.Current.CancellationToken);
        (await probe.GetBoundExchangesAsync("routed", token: TestContext.Current.CancellationToken))
            .ShouldContain("routed");
    }
}
