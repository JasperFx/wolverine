using CoreTests.Runtime;
using Wolverine.Runtime;
using Wolverine.Runtime.Metrics;
using Xunit;

namespace CoreTests.Runtime.Metrics;

// GH-4324: FindAccumulator moved from a linear scan (string compare + full Uri.Equals per entry,
// per message) to an ImHashMap lookup, and the per-destination system/external classification is
// now cached per Uri. These tests pin the identity semantics the swap must preserve.
public class MetricsAccumulatorLookupTests
{
    private readonly MetricsAccumulator theAccumulator = new(new MockWolverineRuntime());

    [Fact]
    public void same_message_type_and_destination_return_the_same_accumulator()
    {
        var one = theAccumulator.FindAccumulator("MyApp.MyMessage", new Uri("tcp://localhost:5000"));
        var two = theAccumulator.FindAccumulator("MyApp.MyMessage", new Uri("tcp://localhost:5000"));

        // Distinct-but-equal Uri instances must land on the same accumulator (value equality)
        two.ShouldBeSameAs(one);
    }

    [Fact]
    public void different_destination_returns_a_different_accumulator()
    {
        var one = theAccumulator.FindAccumulator("MyApp.MyMessage", new Uri("tcp://localhost:5000"));
        var two = theAccumulator.FindAccumulator("MyApp.MyMessage", new Uri("tcp://localhost:5001"));

        two.ShouldNotBeSameAs(one);
        one.Destination.ShouldBe(new Uri("tcp://localhost:5000"));
        two.Destination.ShouldBe(new Uri("tcp://localhost:5001"));
    }

    [Fact]
    public void different_message_type_returns_a_different_accumulator()
    {
        var one = theAccumulator.FindAccumulator("MyApp.MyMessage", new Uri("tcp://localhost:5000"));
        var two = theAccumulator.FindAccumulator("MyApp.OtherMessage", new Uri("tcp://localhost:5000"));

        two.ShouldNotBeSameAs(one);
        one.MessageType.ShouldBe("MyApp.MyMessage");
        two.MessageType.ShouldBe("MyApp.OtherMessage");
    }

    [Theory]
    [InlineData("local://durable", true)]
    [InlineData("rabbitmq://queue/wolverine.response.node1", true)]
    [InlineData("rabbitmq://queue/incoming", false)]
    [InlineData("stub://one", false)]
    public void is_system_endpoint(string uri, bool expected)
    {
        // twice, so both the computing and the cached path are exercised
        WolverineRuntime.IsSystemEndpoint(new Uri(uri)).ShouldBe(expected);
        WolverineRuntime.IsSystemEndpoint(new Uri(uri)).ShouldBe(expected);
    }

    [Fact]
    public void is_system_endpoint_is_false_for_null()
    {
        WolverineRuntime.IsSystemEndpoint(null).ShouldBeFalse();
    }

    [Theory]
    [InlineData("local://durable", false)]
    [InlineData("stub://one", false)]
    [InlineData("rabbitmq://queue/incoming", true)]
    [InlineData("tcp://localhost:5000", true)]
    public void is_external_destination(string uri, bool expected)
    {
        WolverineRuntime.IsExternalDestination(new Uri(uri)).ShouldBe(expected);
        WolverineRuntime.IsExternalDestination(new Uri(uri)).ShouldBe(expected);
    }

    [Fact]
    public void is_external_destination_is_false_for_null()
    {
        WolverineRuntime.IsExternalDestination(null).ShouldBeFalse();
    }
}
