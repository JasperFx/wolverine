using Shouldly;
using Wolverine.Configuration;
using Wolverine.SignalR.Internals;

namespace Wolverine.SignalR.Tests.Internals;

public class SignalRTransportModeTests
{
    private readonly SignalRTransport _transport = new();

    [Fact]
    public void does_not_support_durable_mode()
    {
        _transport.SupportsMode(EndpointMode.Durable).ShouldBeFalse();
    }

    [Fact]
    public void supports_inline_mode()
    {
        _transport.SupportsMode(EndpointMode.Inline).ShouldBeTrue();
    }

    [Fact]
    public void supports_buffered_in_memory_mode()
    {
        _transport.SupportsMode(EndpointMode.BufferedInMemory).ShouldBeTrue();
    }

    [Fact]
    public void setting_durable_mode_throws_exception()
    {
        Should.Throw<InvalidOperationException>(() => _transport.Mode = EndpointMode.Durable);
    }
}
