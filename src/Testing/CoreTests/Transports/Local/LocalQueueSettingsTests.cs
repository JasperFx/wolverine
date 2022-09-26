using System;
using Shouldly;
using Wolverine.Configuration;
using Wolverine.Transports.Local;
using Wolverine.Util;
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
    public void should_set_the_Uri_in_constructor()
    {
        var endpoint = new LocalQueueSettings("foo");
        endpoint.Uri.ShouldBe(new Uri("local://foo"));
    }

    [Fact]
    public void create_by_uri()
    {
        var endpoint = new LocalQueueSettings(new Uri("local://foo"));
        endpoint.Mode.ShouldBe(EndpointMode.BufferedInMemory);
        endpoint.Name.ShouldBe("foo");
    }

    [Fact]
    public void create_by_uri_case_insensitive()
    {
        var endpoint = new LocalQueueSettings(new Uri("local://Foo"));
        endpoint.Mode.ShouldBe(EndpointMode.BufferedInMemory);
        endpoint.Name.ShouldBe("foo");
    }

}
