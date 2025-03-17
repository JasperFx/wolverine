using Shouldly;
using Wolverine.AmazonSns.Internal;

namespace Wolverine.AmazonSns.Tests;

public class AmazonSnsTransportTests
{
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
    public void return_all_endpoints_gets_all_topics()
    {
        var transport = new AmazonSnsTransport();
        var one = transport.Topics["one"];
        var two = transport.Topics["two"];
        var three = transport.Topics["three"];
        
        var endpoints = transport.Endpoints().OfType<AmazonSnsTopic>().ToArray();
        
        endpoints.ShouldContain(x => x.TopicName == one.TopicName);
        endpoints.ShouldContain(x => x.TopicName == two.TopicName);
        endpoints.ShouldContain(x => x.TopicName == three.TopicName);
    }

    [Fact]
    public void findEndpointByUri_should_correctly_find_by_topicname()
    {
        var topicNameInPascalCase = "TestTopic";
        var topicNameLowerCase = "testtopic";
        var transport = new AmazonSnsTransport();
        var testTopic = transport.Topics[topicNameInPascalCase];
        var testTopic2 = transport.Topics[topicNameLowerCase];

        var result = transport.GetOrCreateEndpoint(new Uri($"sns://{topicNameInPascalCase}"));
        transport.Topics.Count.ShouldBe(2);

        result.EndpointName.ShouldBe(topicNameInPascalCase);
    }

    [Fact]
    public void findEndpointByUri_should_correctly_create_endpoint_if_it_doesnt_exist()
    {
        var topicName = "TestTopic";
        var topicName2 = "testtopic";
        var transport = new AmazonSnsTransport();
        transport.Topics.Count.ShouldBe(0);

        var result = transport.GetOrCreateEndpoint(new Uri($"sns://{topicName}"));
        transport.Topics.Count.ShouldBe(1);

        result.EndpointName.ShouldBe(topicName);

        var result2 = transport.GetOrCreateEndpoint(new Uri($"sns://{topicName2}"));
        transport.Topics.Count.ShouldBe(2);
        result2.EndpointName.ShouldBe(topicName2);
    }
}
