using Amazon;
using Shouldly;
using Wolverine.AmazonSqs.Internal;

namespace Wolverine.AmazonSqs.Tests;

public class AmazonSqsTransportTests
{
    [Fact]
    public void resource_uri_uses_explicit_service_url_when_set()
    {
        var transport = new AmazonSqsTransport();
        transport.Config.ServiceURL = "http://localhost:4566";

        transport.ResourceUri.ShouldBe(new Uri("http://localhost:4566"));
    }

    [Fact]
    public void resource_uri_falls_back_to_region_when_service_url_not_set()
    {
        // Reproduces #3115 -- setting only RegionEndpoint used to throw
        // ArgumentNullException from new Uri(Config.ServiceURL)
        var transport = new AmazonSqsTransport();
        transport.Config.RegionEndpoint = RegionEndpoint.USWest2;

        transport.ResourceUri.ShouldBe(new Uri("https://sqs.us-west-2.amazonaws.com"));
    }

    [Fact]
    public void resource_uri_does_not_throw_when_neither_service_url_nor_region_set()
    {
        var transport = new AmazonSqsTransport();

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
        new AmazonSqsTransport().SanitizeIdentifier(identifier)
            .ShouldBe(expected);
    }

    [Fact]
    public void return_all_endpoints_gets_dead_letter_queue_too()
    {
        var transport = new AmazonSqsTransport();
        var one = transport.Queues["one"];
        var two = transport.Queues["two"];
        var three = transport.Queues["three"];
        three.IsListener = true;

        one.DeadLetterQueueName = null;
        two.DeadLetterQueueName = "two-dead-letter-queue";
        two.IsListener = true;

        var endpoints = transport.Endpoints().OfType<AmazonSqsQueue>().ToArray();

        endpoints.ShouldContain(x => x.QueueName == AmazonSqsTransport.DeadLetterQueueName);
        endpoints.ShouldContain(x => x.QueueName == "two-dead-letter-queue");
        endpoints.ShouldContain(x => x.QueueName == "one");
        endpoints.ShouldContain(x => x.QueueName == "two");
        endpoints.ShouldContain(x => x.QueueName == "three");
    }

    [Fact]
    public void findEndpointByUri_should_correctly_find_by_queuename()
    {
        string queueNameInPascalCase = "TestQueue";
        string queueNameLowerCase = "testqueue";
        var transport = new AmazonSqsTransport();
        var testQueue = transport.Queues[queueNameInPascalCase];
        var testQueue2 = transport.Queues[queueNameLowerCase];

        var result = transport.GetOrCreateEndpoint(new Uri($"sqs://{queueNameInPascalCase}"));
        transport.Queues.Count.ShouldBe(2);

        result.EndpointName.ShouldBe(queueNameInPascalCase);
    }

    [Fact]
    public void findEndpointByUri_should_correctly_create_endpoint_if_it_doesnt_exist()
    {
        string queueName = "TestQueue";
        string queueName2 = "testqueue";
        var transport = new AmazonSqsTransport();
        transport.Queues.Count.ShouldBe(0);

        var result = transport.GetOrCreateEndpoint(new Uri($"sqs://{queueName}"));
        transport.Queues.Count.ShouldBe(1);

        result.EndpointName.ShouldBe(queueName);

        var result2 = transport.GetOrCreateEndpoint(new Uri($"sqs://{queueName2}"));
        transport.Queues.Count.ShouldBe(2);
        result2.EndpointName.ShouldBe(queueName2);
    }
}
// GH-3763. Conventional routing derives queue names from message type names, so it can hand SQS a
// name it rejects outright -- and because broker initialization provisions every queue together,
// one bad name fails startup for every conventionally-routed host in the assembly. See GH-3786 for
// the same defect on Azure Service Bus.
public class sanitizing_sqs_queue_names
{
    // "Can only include alphanumeric characters, hyphens, or underscores. 1 to 80 in length"
    [Theory]
    [InlineData("Wolverine.Bugs.BatchedItem[]", "Wolverine-Bugs-BatchedItem__")]
    [InlineData("Wolverine.Envelope`1[System.String]", "Wolverine-Envelope_1_System-String_")]
    [InlineData("Outer+Inner", "Outer_Inner")]
    [InlineData("has spaces", "has_spaces")]
    public void illegal_characters_are_substituted(string identifier, string expected)
    {
        AmazonSqsTransport.SanitizeSqsName(identifier).ShouldBe(expected);
    }

    // Substituting rather than stripping is what keeps distinct type names distinct.
    [Fact]
    public void sanitizing_does_not_collide_an_array_type_with_its_element_type()
    {
        AmazonSqsTransport.SanitizeSqsName("BatchedItem[]")
            .ShouldNotBe(AmazonSqsTransport.SanitizeSqsName("BatchedItem"));
    }

    // No name that works today may change: a name SQS rejects could never have been provisioned in
    // the first place, so this must stay a pure no-op for legal names of legal length.
    [Theory]
    [InlineData("Wolverine.Bugs.BatchedItem", "Wolverine-Bugs-BatchedItem")]
    [InlineData("two-dead-letter-queue", "two-dead-letter-queue")]
    [InlineData("wolverine_retries_MyService", "wolverine_retries_MyService")]
    [InlineData("wolverine.retries.MyService.fifo", "wolverine-retries-MyService.fifo")]
    public void legal_identifiers_are_left_alone(string identifier, string expected)
    {
        AmazonSqsTransport.SanitizeSqsName(identifier).ShouldBe(expected);
    }

    // The case that actually broke CI: "shazaam-" + a namespace-qualified type name is 81 characters.
    [Fact]
    public void an_overlong_name_is_brought_under_the_limit()
    {
        var name = AmazonSqsTransport.SanitizeSqsName(
            "shazaam-Wolverine.AmazonSqs.Tests.ConventionalRouting.SqsHandlerTypeNamingMessage");

        name.Length.ShouldBe(AmazonSqsTransport.MaximumQueueNameLength);
        name.ShouldStartWith("shazaam-Wolverine-AmazonSqs-Tests-ConventionalRouting-SqsHandlerType");
    }

    // Truncation alone would collide two long names that share a prefix -- and namespace-qualified
    // type names very often do.
    [Fact]
    public void two_overlong_names_sharing_a_prefix_stay_distinct()
    {
        var prefix = new string('a', AmazonSqsTransport.MaximumQueueNameLength);

        AmazonSqsTransport.SanitizeSqsName(prefix + "One")
            .ShouldNotBe(AmazonSqsTransport.SanitizeSqsName(prefix + "Two"));
    }

    // The digest has to be stable across processes and machines, which rules out GetHashCode().
    [Fact]
    public void truncation_is_deterministic()
    {
        var identifier = new string('b', 200);

        AmazonSqsTransport.SanitizeSqsName(identifier)
            .ShouldBe("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb-aaebc35c");
    }

    // AWS requires the .fifo suffix, and it counts against the 80 character budget.
    [Fact]
    public void the_fifo_suffix_survives_truncation()
    {
        var name = AmazonSqsTransport.SanitizeSqsName(new string('c', 200) + ".fifo");

        name.Length.ShouldBe(AmazonSqsTransport.MaximumQueueNameLength);
        name.ShouldEndWith(".fifo");
    }
}
