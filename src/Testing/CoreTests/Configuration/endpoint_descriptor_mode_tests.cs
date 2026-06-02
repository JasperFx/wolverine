using Shouldly;
using Wolverine.Configuration;
using Wolverine.Configuration.Capabilities;
using Wolverine.Transports.Local;
using Xunit;

namespace CoreTests.Configuration;

/// <summary>
/// Locks down GH-3009: <see cref="Endpoint.Mode"/> and <see cref="Endpoint.IsListener"/>
/// are lifted onto <see cref="EndpointDescriptor"/> as first-class typed fields (the same
/// shape as BrokerRole / EndpointRole), and the duplicate generic Properties rows the base
/// OptionsDescription ctor reflects off the Endpoint are dropped so the payload doesn't
/// ship them twice. CritterWatch reads the typed fields at the service-overview level.
/// </summary>
public class endpoint_descriptor_mode_tests
{
    [Fact]
    public void lifts_mode_and_is_listener_as_typed_fields()
    {
        var queue = new LocalQueue("queue-mode")
        {
            Mode = EndpointMode.Durable,
            IsListener = true
        };

        var descriptor = new EndpointDescriptor(queue);

        descriptor.Mode.ShouldBe(EndpointMode.Durable);
        descriptor.IsListener.ShouldBeTrue();
    }

    [Fact]
    public void reflects_buffered_and_non_listener_values()
    {
        var queue = new LocalQueue("queue-buffered")
        {
            Mode = EndpointMode.BufferedInMemory,
            IsListener = false
        };

        var descriptor = new EndpointDescriptor(queue);

        descriptor.Mode.ShouldBe(EndpointMode.BufferedInMemory);
        descriptor.IsListener.ShouldBeFalse();
    }

    [Fact]
    public void does_not_duplicate_mode_and_is_listener_in_properties()
    {
        var queue = new LocalQueue("queue-no-dupes")
        {
            Mode = EndpointMode.Durable,
            IsListener = true
        };

        var descriptor = new EndpointDescriptor(queue);

        // Promoted to typed fields — the generic Properties rows must be gone so we don't double-ship.
        descriptor.Properties.ShouldNotContain(x => x.Name == nameof(Endpoint.Mode));
        descriptor.Properties.ShouldNotContain(x => x.Name == nameof(Endpoint.IsListener));
    }
}
