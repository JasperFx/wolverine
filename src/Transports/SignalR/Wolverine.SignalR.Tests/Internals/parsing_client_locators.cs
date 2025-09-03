using Shouldly;
using Wolverine.SignalR.Internals;

namespace Wolverine.SignalR.Tests.Internals;

public class parsing_client_locators
{
    private void AssertRoundTripParsing(WebSocketRouting.IClientProxyLocator locator)
    {
        var locator2 = WebSocketRouting.ParseLocator(locator.ToString());
        locator2.ShouldBe(locator);
    }

    [Fact]
    public void ignore_anything_that_does_not_match()
    {
        WebSocketRouting.ParseLocator("garbage").ShouldBeOfType<WebSocketRouting.All>();
        WebSocketRouting.ParseLocator(null).ShouldBeOfType<WebSocketRouting.All>();
        WebSocketRouting.ParseLocator(string.Empty).ShouldBeOfType<WebSocketRouting.All>();
        WebSocketRouting.ParseLocator("unknown=what?").ShouldBeOfType<WebSocketRouting.All>();
    }

    [Fact]
    public void parse_all()
    {
        AssertRoundTripParsing(new WebSocketRouting.All());
    }

    [Fact]
    public void parse_connection()
    {
        AssertRoundTripParsing(new WebSocketRouting.Connection("something"));
    }

    [Fact]
    public void parse_group()
    {
        AssertRoundTripParsing(new WebSocketRouting.Group("group1"));
    }
}