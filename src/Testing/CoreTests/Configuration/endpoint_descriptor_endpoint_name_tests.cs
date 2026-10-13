using Shouldly;
using Wolverine.Configuration;
using Wolverine.Configuration.Capabilities;
using Wolverine.Transports.Local;
using Xunit;

namespace CoreTests.Configuration;

/// <summary>
/// GH-4887: <see cref="Endpoint.EndpointName"/> is lifted onto <see cref="EndpointDescriptor"/> as a first-class
/// typed field, the same shape as GH-3009's Mode / IsListener, and its generic Properties row is dropped. It is the
/// endpoint's display name on every CritterWatch list, while the bulky Properties tree is only fetched on demand.
/// </summary>
public class endpoint_descriptor_endpoint_name_tests
{
    [Fact]
    public void lifts_an_explicit_endpoint_name_as_a_typed_field()
    {
        var queue = new LocalQueue("incoming") { EndpointName = "Incoming Orders" };

        new EndpointDescriptor(queue).EndpointName.ShouldBe("Incoming Orders");
    }

    [Fact]
    public void falls_back_to_the_uri_like_the_endpoint_does()
    {
        var queue = new LocalQueue("unnamed");

        var descriptor = new EndpointDescriptor(queue);

        descriptor.EndpointName.ShouldBe(queue.EndpointName);
        descriptor.EndpointName.ShouldNotBeNullOrEmpty();
    }

    [Fact]
    public void does_not_duplicate_endpoint_name_in_properties()
    {
        var queue = new LocalQueue("no-dupes") { EndpointName = "No Dupes" };

        new EndpointDescriptor(queue).Properties.ShouldNotContain(x => x.Name == nameof(Endpoint.EndpointName));
    }
}
