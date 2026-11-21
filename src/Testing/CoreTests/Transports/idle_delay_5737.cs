using JasperFx.Core;
using Shouldly;
using Wolverine.Configuration;
using Wolverine.Runtime;
using Wolverine.Transports;
using Wolverine.Transports.Local;
using Wolverine.Transports.Sending;
using Xunit;

namespace CoreTests.Transports;

// GH-5737. A polling listener waits a fixed 250ms after every empty receive, so a very quiet queue still costs
// thousands of (billable) empty receives a day. IdleDelay() makes that wait configurable for every endpoint whose
// listener polls, through one core option that each transport's listener reads with IdleDelays.For().
public class idle_delay_5737
{
    [Fact]
    public void the_default_is_250_milliseconds()
    {
        IdleDelays.Default.ShouldBe(250.Milliseconds());
    }

    [Fact]
    public void an_endpoint_that_does_not_poll_gets_the_default()
    {
        IdleDelays.For(new LocalQueue("one")).ShouldBe(IdleDelays.Default);
    }

    [Fact]
    public void a_polling_endpoint_with_nothing_configured_gets_the_default()
    {
        IdleDelays.For(new PollingEndpoint()).ShouldBe(IdleDelays.Default);
    }

    [Fact]
    public void a_polling_endpoint_uses_its_configured_idle_delay()
    {
        var endpoint = new PollingEndpoint { IdleDelay = 5.Minutes() };

        IdleDelays.For(endpoint).ShouldBe(5.Minutes());
    }

    [Fact]
    public void configure_the_idle_delay_through_the_listener_configuration()
    {
        var endpoint = new PollingEndpoint();

        var configuration = new ListenerConfiguration(endpoint);
        configuration.IdleDelay(5.Minutes());
        ((IDelayedEndpointConfiguration)configuration).Apply();

        endpoint.IdleDelay.ShouldBe(5.Minutes());
        IdleDelays.For(endpoint).ShouldBe(5.Minutes());
    }

    [Fact]
    public void zero_is_a_valid_idle_delay()
    {
        var endpoint = new PollingEndpoint();

        var configuration = new ListenerConfiguration(endpoint);
        configuration.IdleDelay(TimeSpan.Zero);
        ((IDelayedEndpointConfiguration)configuration).Apply();

        IdleDelays.For(endpoint).ShouldBe(TimeSpan.Zero);
    }

    [Fact]
    public void a_negative_idle_delay_is_refused()
    {
        Should.Throw<ArgumentOutOfRangeException>(() =>
            new ListenerConfiguration(new PollingEndpoint()).IdleDelay(-1.Seconds()));
    }

    [Fact]
    public void an_idle_delay_longer_than_a_listener_can_wait_is_refused()
    {
        Should.Throw<ArgumentOutOfRangeException>(() =>
            new ListenerConfiguration(new PollingEndpoint()).IdleDelay(IdleDelays.Maximum + 1.Milliseconds()));
    }

    [Fact]
    public void an_endpoint_that_does_not_poll_refuses_the_setting_rather_than_ignoring_it()
    {
        var ex = Should.Throw<NotSupportedException>(() =>
            new ListenerConfiguration(new NonPollingEndpoint()).IdleDelay(5.Minutes()));

        ex.Message.ShouldContain("does not support IdleDelay()");
    }

    private class PollingEndpoint() : Endpoint(new Uri("polling://one"), EndpointRole.Application), ISupportIdleDelay
    {
        public TimeSpan? IdleDelay { get; set; }

        public override ValueTask<IListener> BuildListenerAsync(IWolverineRuntime runtime, IReceiver receiver)
            => throw new NotSupportedException();

        protected override ISender CreateSender(IWolverineRuntime runtime) => throw new NotSupportedException();
    }

    private class NonPollingEndpoint() : Endpoint(new Uri("pushing://one"), EndpointRole.Application)
    {
        public override ValueTask<IListener> BuildListenerAsync(IWolverineRuntime runtime, IReceiver receiver)
            => throw new NotSupportedException();

        protected override ISender CreateSender(IWolverineRuntime runtime) => throw new NotSupportedException();
    }
}
