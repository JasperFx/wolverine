using Google.Api.Gax;
using Shouldly;
using Wolverine.Configuration.Capabilities;
using Wolverine.Pubsub;
using Xunit;

namespace Wolverine.Pubsub.Tests;

// GH-3269: the GCP Pub/Sub summary is the project id (with an emulator marker when applicable).
public class broker_connection_summary_3269
{
    [Fact]
    public void describe_endpoint_reports_the_project_id()
    {
        var transport = new PubsubTransport { ProjectId = "my-project" };
        transport.DescribeEndpoint().ShouldBe("project: my-project");
    }

    [Fact]
    public void describe_endpoint_marks_the_emulator()
    {
        var transport = new PubsubTransport { ProjectId = "my-project", EmulatorDetection = EmulatorDetection.EmulatorOnly };
        transport.DescribeEndpoint().ShouldBe("project: my-project (emulator)");
    }

    [Fact]
    public void broker_description_endpoint_is_populated()
    {
        var transport = new PubsubTransport { ProjectId = "my-project" };
        new BrokerDescription(transport).Endpoint.ShouldBe("project: my-project");
    }
}
