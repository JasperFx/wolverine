using System.Linq;
using Wolverine.Transports;
using Wolverine.Transports.Local;
using Wolverine.Util;
using Xunit;

namespace CoreTests.Transports.Local;

public class LocalTransportTests
{
    [Theory]
    [InlineData(TransportConstants.Default)]
    [InlineData(TransportConstants.Replies)]
    public void has_default_queues(string queueName)
    {
        new LocalTransport()
            .AllQueues().Any(x => x.EndpointName == queueName)
            .ShouldBeTrue();
    }

    [Fact]
    public void reply_endpoint_is_replies()
    {
        new LocalTransport()
            .ReplyEndpoint()
            .Uri.ShouldBe(TransportConstants.RepliesUri);
    }

    [Fact]
    public void forces_the_queue_name_to_be_lower_case()
    {
        new LocalQueue("Foo")
            .EndpointName.ShouldBe("foo");
    }


    [Fact]
    public void case_insensitive_queue_find()
    {
        var transport = new LocalTransport();

        transport.QueueFor("One")
            .ShouldBeSameAs(transport.QueueFor("one"));
    }


    [Fact]
    public void queue_at_extension()
    {
        var uri = LocalTransport.AtQueue(TransportConstants.LocalUri, "one");

        LocalTransport.QueueName(uri).ShouldBe("one");
    }


    [Fact]
    public void queue_at_other_queue()
    {
        var uri = LocalTransport.AtQueue("tcp://localhost:2222".ToUri(), "one");

        LocalTransport.QueueName(uri).ShouldBe("one");
    }

    [Fact]
    public void fall_back_to_the_default_queue_if_no_segments()
    {
        LocalTransport.QueueName("tcp://localhost:2222".ToUri()).ShouldBe(TransportConstants.Default);
    }


    [Fact]
    public void use_the_last_segment_if_it_exists()
    {
        LocalTransport.QueueName("tcp://localhost:2222/incoming".ToUri()).ShouldBe("incoming");
    }
}