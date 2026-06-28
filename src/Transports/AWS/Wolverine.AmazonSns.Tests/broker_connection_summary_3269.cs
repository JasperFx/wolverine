using Amazon;
using Shouldly;
using Wolverine.AmazonSns.Internal;
using Wolverine.Configuration.Capabilities;
using Xunit;

namespace Wolverine.AmazonSns.Tests;

// GH-3269: the SNS summary is the region (or an explicit ServiceURL such as LocalStack).
public class broker_connection_summary_3269
{
    [Fact]
    public void describe_endpoint_reports_the_region()
    {
        var transport = new AmazonSnsTransport();
        transport.SnsConfig.RegionEndpoint = RegionEndpoint.USEast1;

        transport.DescribeEndpoint().ShouldBe("us-east-1");
    }

    [Fact]
    public void describe_endpoint_prefers_an_explicit_service_url()
    {
        var transport = new AmazonSnsTransport();
        transport.SnsConfig.ServiceURL = "http://localhost:4566";

        // The AWS SDK may normalize the URL (e.g. a trailing slash); DescribeEndpoint reports it verbatim.
        transport.DescribeEndpoint().ShouldBe(transport.SnsConfig.ServiceURL);
        transport.DescribeEndpoint()!.ShouldContain("localhost:4566");
    }

    [Fact]
    public void broker_description_endpoint_is_populated()
    {
        var transport = new AmazonSnsTransport();
        transport.SnsConfig.RegionEndpoint = RegionEndpoint.EUWest2;

        new BrokerDescription(transport).Endpoint.ShouldBe("eu-west-2");
    }
}
