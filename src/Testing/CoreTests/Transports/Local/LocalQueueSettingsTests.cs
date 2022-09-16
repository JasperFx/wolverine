using System;
using Shouldly;
using Wolverine.Configuration;
using Wolverine.Transports.Local;
using Wolverine.Util;
using Xunit;

namespace CoreTests.Transports.Local;

public class LocalQueueSettingsTests
{
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

    [Fact]
    public void create_by_uri_durable()
    {
        var endpoint = new LocalQueueSettings(new Uri("local://durable/foo"));
        endpoint.Mode.ShouldBe(EndpointMode.Durable);
        endpoint.Name.ShouldBe("foo");
    }

    [Fact]
    public void reply_uri_when_durable()
    {
        var endpoint = new LocalQueueSettings("foo");
        endpoint.Mode = EndpointMode.Durable;

        endpoint.CorrectedUriForReplies().ShouldBe("local://durable/foo".ToUri());
    }

    [Fact]
    public void replay_uri_when_not_durable()
    {
        var endpoint = new LocalQueueSettings("foo");
        endpoint.Mode = EndpointMode.BufferedInMemory;

        endpoint.CorrectedUriForReplies().ShouldBe("local://foo".ToUri());
    }
}
