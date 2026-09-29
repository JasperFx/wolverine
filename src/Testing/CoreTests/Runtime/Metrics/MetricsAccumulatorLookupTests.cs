using CoreTests.Runtime;
using Microsoft.Extensions.Hosting;
using Wolverine;
using Wolverine.Runtime;
using Wolverine.Runtime.Metrics;
using Wolverine.Tracking;
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

    // GH-4665: the System classification now asks the endpoint for its EndpointRole, so it needs a real
    // runtime rather than a mock. MetricsTests/is_system_endpoint_tests covers that half exhaustively,
    // including the user-local-queue case this bug was about; these keep the External half pinned, which
    // is still a pure function of the scheme.
    [Theory]
    [InlineData("local://durable", false)]
    [InlineData("stub://one", false)]
    [InlineData("rabbitmq://queue/incoming", true)]
    [InlineData("tcp://localhost:5000", true)]
    public async Task is_external_destination(string uri, bool expected)
    {
        using var host = await Host.CreateDefaultBuilder().UseWolverine().StartAsync(TestContext.Current.CancellationToken);
        var runtime = host.GetRuntime();

        // twice, so both the computing and the cached path are exercised
        runtime.IsExternalDestination(new Uri(uri)).ShouldBe(expected);
        runtime.IsExternalDestination(new Uri(uri)).ShouldBe(expected);

        await host.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task is_external_destination_is_false_for_null()
    {
        using var host = await Host.CreateDefaultBuilder().UseWolverine().StartAsync(TestContext.Current.CancellationToken);

        host.GetRuntime().IsExternalDestination(null).ShouldBeFalse();

        await host.StopAsync(TestContext.Current.CancellationToken);
    }
}
