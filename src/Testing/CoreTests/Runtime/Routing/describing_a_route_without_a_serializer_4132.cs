using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine;
using Wolverine.ComplianceTests;
using Wolverine.ComplianceTests.Compliance;
using Wolverine.Runtime;
using Wolverine.Runtime.Partitioning;
using Wolverine.Runtime.Routing;
using Wolverine.Tracking;
using Wolverine.Transports.Local;
using Xunit;

namespace CoreTests.Runtime.Routing;

// GH-4132. Endpoint.Compile() is what assigns DefaultSerializer, and the MessageRoute constructor
// only demands a serializer when it is *not* running under WolverineSystemPart.WithinDescription --
// the description path deliberately takes the null-forgiving branch so that diagnostics can describe
// a topology without forcing sending agents into existence. That leaves Serializer null on any route
// built during description against an endpoint the runtime never compiled, and Describe() then blew
// up with a NullReferenceException -- taking out `describe-routing` for the whole application, not
// just the offending route. WolverineDiagnosticsCommand already null-guards route.Serializer when it
// reads content types directly; Describe() now agrees with it.
public class describing_a_route_without_a_serializer_4132
{
    [Fact]
    public async Task describe_falls_back_to_the_default_content_type()
    {
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine().StartAsync(TestContext.Current.CancellationToken);

        var runtime = host.GetRuntime();

        // Never handed to a transport, so Compile() never ran and DefaultSerializer is null
        var uncompiled = new LocalQueue("never-compiled-4132");
        uncompiled.DefaultSerializer.ShouldBeNull();

        WolverineSystemPart.WithinDescription = true;
        try
        {
            var route = MessageRoute.For(typeof(Message1), uncompiled, runtime);
            route.Serializer.ShouldBeNull();

            var descriptor = route.Describe();

            descriptor.Endpoint.ShouldBe(uncompiled.Uri);
            descriptor.ContentType.ShouldBe("application/json");
        }
        finally
        {
            WolverineSystemPart.WithinDescription = false;
        }
    }

    [Fact]
    public async Task a_globally_partitioned_route_describes_all_of_its_slots()
    {
        // The reported stack came through GlobalPartitionedRoute.Describe(), which maps over every
        // slot route -- so one uncompiled slot took down the whole topology's description.
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine().StartAsync(TestContext.Current.CancellationToken);

        var runtime = host.GetRuntime();

        WolverineSystemPart.WithinDescription = true;
        try
        {
            var slots = new IMessageRoute[]
            {
                MessageRoute.For(typeof(Message1), new LocalQueue("slot-one-4132"), runtime),
                MessageRoute.For(typeof(Message1), new LocalQueue("slot-two-4132"), runtime)
            };

            var route = new GlobalPartitionedRoute(new Uri("shard://local/slots-4132"),
                runtime.Options.MessagePartitioning, slots, [], [], nativeAcks: true);

            var descriptor = route.Describe();

            descriptor.Description.ShouldBe("Global Partitioned");
            descriptor.Partitions.Length.ShouldBe(2);
            descriptor.Partitions.All(x => x.ContentType == "application/json").ShouldBeTrue();
        }
        finally
        {
            WolverineSystemPart.WithinDescription = false;
        }
    }
}
