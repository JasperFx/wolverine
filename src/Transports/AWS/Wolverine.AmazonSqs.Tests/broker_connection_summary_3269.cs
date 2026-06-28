using Amazon;
using Shouldly;
using Wolverine.AmazonSqs.Internal;
using Wolverine.Configuration.Capabilities;
using Xunit;

namespace Wolverine.AmazonSqs.Tests;

// GH-3269: the SQS summary is the region (or an explicit ServiceURL such as LocalStack). AWS credentials are supplied
// separately (CredentialSource), never as a string on the transport.
public class broker_connection_summary_3269
{
    [Fact]
    public void describe_endpoint_reports_the_region()
    {
        var transport = new AmazonSqsTransport();
        transport.Config.RegionEndpoint = RegionEndpoint.USEast1;

        transport.DescribeEndpoint().ShouldBe("us-east-1");
    }

    [Fact]
    public void describe_endpoint_prefers_an_explicit_service_url()
    {
        var transport = new AmazonSqsTransport();
        transport.Config.ServiceURL = "http://localhost:4566";

        // The AWS SDK may normalize the URL (e.g. a trailing slash); DescribeEndpoint reports it verbatim.
        transport.DescribeEndpoint().ShouldBe(transport.Config.ServiceURL);
        transport.DescribeEndpoint()!.ShouldContain("localhost:4566");
    }

    [Fact]
    public void broker_description_endpoint_is_populated()
    {
        var transport = new AmazonSqsTransport();
        transport.Config.RegionEndpoint = RegionEndpoint.EUWest2;

        new BrokerDescription(transport).Endpoint.ShouldBe("eu-west-2");
    }
}
