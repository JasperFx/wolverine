using Shouldly;
using Wolverine.Configuration;
using Wolverine.Configuration.Capabilities;
using Wolverine.Transports.Local;
using Xunit;

namespace CoreTests.Configuration;

/// <summary>
/// Locks down GH-3104: every endpoint declares a transport-agnostic
/// <see cref="Endpoint.DeadLetterStorage"/>, lifted onto
/// <see cref="EndpointDescriptor.DeadLetterStorage"/> as a first-class typed field so monitoring
/// tools can introspect where an endpoint's dead letters go without transport-specific knowledge.
/// Endpoints with no native dead letter queue (the core/local default) report
/// <see cref="DeadLetterStorageMode.Durable"/>.
/// </summary>
public class endpoint_descriptor_dead_letter_storage_tests
{
    [Fact]
    public void base_endpoint_default_is_durable()
    {
        var queue = new LocalQueue("dlq-default");

        queue.DeadLetterStorage.ShouldBe(DeadLetterStorageMode.Durable);
    }

    [Fact]
    public void descriptor_surfaces_dead_letter_storage()
    {
        var queue = new LocalQueue("dlq-descriptor");

        var descriptor = new EndpointDescriptor(queue);

        descriptor.DeadLetterStorage.ShouldBe(DeadLetterStorageMode.Durable);
    }

    [Fact]
    public void does_not_duplicate_dead_letter_storage_in_properties()
    {
        var queue = new LocalQueue("dlq-no-dupes");

        var descriptor = new EndpointDescriptor(queue);

        // Promoted to a typed field — the generic Properties row must be gone so we don't double-ship.
        descriptor.Properties.ShouldNotContain(x => x.Name == nameof(Endpoint.DeadLetterStorage));
    }
}
