using System;
using CoreTests.Runtime;
using JasperFx.Core;
using Wolverine.Configuration;
using Wolverine.Transports.Local;
using Xunit;

namespace CoreTests.Transports.Local;

public class LocalQueueSettingsTests
{
    [Theory]
    [InlineData(EndpointMode.Durable)]
    [InlineData(EndpointMode.Inline)]
    [InlineData(EndpointMode.BufferedInMemory)]
    public void should_not_enforce_back_pressure_no_matter_what(EndpointMode mode)
    {
        var endpoint = new LocalQueueSettings("foo")
        {
            Mode = mode
        };

        endpoint.ShouldEnforceBackPressure().ShouldBeFalse();
    }

    [Fact]
    public void should_use_the_queue_name_as_endpoint_name()
    {
        var endpoint = new LocalQueueSettings("foo");

        endpoint.EndpointName.ShouldBe("foo");
    }

    [Fact]
    public void should_set_the_Uri_in_constructor()
    {
        var endpoint = new LocalQueueSettings("foo");
        endpoint.Uri.ShouldBe(new Uri("local://foo"));
    }

    [Fact]
    public void create_by_uri()
    {
        var endpoint = new LocalQueueSettings("foo");
        endpoint.Mode.ShouldBe(EndpointMode.BufferedInMemory);
        endpoint.EndpointName.ShouldBe("foo");
    }

    [Fact]
    public void create_by_uri_case_insensitive()
    {
        var endpoint = new LocalQueueSettings("Foo");
        endpoint.Mode.ShouldBe(EndpointMode.BufferedInMemory);
        endpoint.EndpointName.ShouldBe("foo");
    }

    [Fact]
    public void configure_circuit_breaker_options_with_defaults()
    {
        var endpoint = new LocalQueueSettings("foo");
        new LocalQueueConfiguration(endpoint).CircuitBreaker();
        endpoint.Compile(new MockWolverineRuntime());

        endpoint.CircuitBreakerOptions.ShouldNotBeNull();
    }


    [Fact]
    public void configure_circuit_breaker_options_with_explicit_config()
    {
        var endpoint = new LocalQueueSettings("Foo");
        new LocalQueueConfiguration(endpoint).CircuitBreaker(cb => { cb.PauseTime = 23.Minutes(); });

        endpoint.Compile(new MockWolverineRuntime());

        endpoint.CircuitBreakerOptions.PauseTime.ShouldBe(23.Minutes());

        endpoint.CircuitBreakerOptions.ShouldNotBeNull();
    }
}