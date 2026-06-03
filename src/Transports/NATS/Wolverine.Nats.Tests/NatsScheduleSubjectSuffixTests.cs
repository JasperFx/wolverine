using Shouldly;
using Wolverine.Nats.Configuration;
using Wolverine.Nats.Internal;
using Xunit;

namespace Wolverine.Nats.Tests;

public class NatsScheduleSubjectSuffixTests
{
    private static NatsEndpoint EndpointFor(string subject = "orders.created")
    {
        var transport = new NatsTransport();
        return (NatsEndpoint)transport.GetOrCreateEndpoint(NatsEndpointUri.Subject(subject));
    }

    [Fact]
    public void default_suffix_is_scheduled()
    {
        EndpointFor().ScheduleSubjectSuffix.ShouldBe(".scheduled");
    }

    [Theory]
    [InlineData(".scheduled")]
    [InlineData(".override-scheduled")]
    [InlineData(".control")]
    public void use_schedule_subject_suffix_round_trips_to_endpoint(string suffix)
    {
        var endpoint = EndpointFor();
        var configuration = new NatsSubscriberConfiguration(endpoint);

        configuration.UseScheduleSubjectSuffix(suffix);
        // Callbacks are buffered in the IDelayedEndpointConfiguration base; Apply() mirrors what the
        // runtime does at endpoint compile time.
        ((Wolverine.Configuration.IDelayedEndpointConfiguration)configuration).Apply();

        endpoint.ScheduleSubjectSuffix.ShouldBe(suffix);
    }
}
