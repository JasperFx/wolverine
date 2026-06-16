 using Amazon;
using Shouldly;
using Wolverine.AmazonSns.Internal;

namespace Wolverine.AmazonSns.Tests;

public class AmazonSnsTransportTests
{
    [Fact]
    public void resource_uri_uses_explicit_service_url_when_set()
    {
        var transport = new AmazonSnsTransport();
        transport.SnsConfig.ServiceURL = "http://localhost:4566";

        transport.ResourceUri.ShouldBe(new Uri("http://localhost:4566"));
    }

    [Fact]
    public void resource_uri_falls_back_to_region_when_service_url_not_set()
    {
        // Reproduces #3115 -- setting only RegionEndpoint used to throw
        // ArgumentNullException from new Uri(SnsConfig.ServiceURL)
        var transport = new AmazonSnsTransport();
        transport.SnsConfig.RegionEndpoint = RegionEndpoint.USWest2;

        transport.ResourceUri.ShouldBe(new Uri("https://sns.us-west-2.amazonaws.com"));
    }

    [Fact]
    public void resource_uri_does_not_throw_when_neither_service_url_nor_region_set()
    {
        var transport = new AmazonSnsTransport();

        // Should not throw an ArgumentNullException
        Should.NotThrow(() => transport.ResourceUri);
    }

    [Theory]
    [InlineData("good.fifo", "good.fifo")]
    [InlineData("good", "good")]
    [InlineData("foo.bar", "foo-bar")]
    [InlineData("foo.bar.fifo", "foo-bar.fifo")]
    public void sanitizing_identifiers(string identifier, string expected)
    {
        new AmazonSnsTransport().SanitizeIdentifier(identifier)
            .ShouldBe(expected);
    }
    

    [Fact]
    public void findEndpointByUri_should_correctly_find_by_topicName()
    {
        string topicNameInPascalCase = "TestTopic";
        string topicNameLowerCase = "testtopic";
        var transport = new AmazonSnsTransport();
        var testTopic = transport.Topics[topicNameInPascalCase];
        var testTopic2 = transport.Topics[topicNameLowerCase];

        var result = transport.GetOrCreateEndpoint(new Uri($"{AmazonSnsTransport.SnsProtocol}://{topicNameInPascalCase}"));
        transport.Topics.Count.ShouldBe(2);

        result.EndpointName.ShouldBe(topicNameInPascalCase);
    }

    [Fact]
    public void findEndpointByUri_should_correctly_create_endpoint_if_it_doesnt_exist()
    {
        string topicName = "TestTopic";
        string topicName2 = "testtopic";
        var transport = new AmazonSnsTransport();
        transport.Topics.Count.ShouldBe(0);

        var result = transport.GetOrCreateEndpoint(new Uri($"{AmazonSnsTransport.SnsProtocol}://{topicName}"));
        transport.Topics.Count.ShouldBe(1);

        result.EndpointName.ShouldBe(topicName);

        var result2 = transport.GetOrCreateEndpoint(new Uri($"{AmazonSnsTransport.SnsProtocol}://{topicName2}"));
        transport.Topics.Count.ShouldBe(2);
        result2.EndpointName.ShouldBe(topicName2);
    }
}
