using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine;
using Wolverine.Runtime;
using Wolverine.Runtime.RemoteInvocation;
using Wolverine.Runtime.Routing;
using Wolverine.Runtime.Serialization;
using Wolverine.Tracking;
using Wolverine.Transports.Local;
using Xunit;

namespace CoreTests.Runtime.Routing;

// GH-4232. IntrinsicSerializer seeds the framework's own ISerializable types into its cache by DIRECT
// construction, because closing IntrinsicSerializer<T> reflectively throws MissingMethodException in a
// Native AOT image — the closed generic's constructor metadata is trimmed. MessageRoute closed the
// generic itself instead of asking the cache, so every AOT-published app that configured ANY external
// endpoint died during startup building the route for Acknowledgement / FailureAcknowledgement, even
// though a perfectly good instance was already sitting in that cache.
//
// The AOT publish smoke could not catch it because it only dispatched locally and so never built an
// external route; it now configures a sending endpoint (verified: reverting the MessageRoute fix makes
// that native binary exit non-zero again). This test is the cheap JIT-side guard on the same invariant:
// route construction must go THROUGH the cache, which is observable as reference equality.
public class intrinsic_serializer_routes_through_the_cache_4232
{
    [Fact]
    public async Task a_route_for_a_framework_serializable_type_reuses_the_seeded_serializer()
    {
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine().StartAsync(TestContext.Current.CancellationToken);

        var runtime = host.GetRuntime();

        var route = MessageRoute.For(typeof(FailureAcknowledgement), new LocalQueue("gh-4232"), runtime);

        // Not merely "a serializer for the right type" — the very instance the constructor seeded.
        // A route that closes the open generic itself produces a fresh one, which is the AOT crash.
        route.Serializer.ShouldBeSameAs(
            IntrinsicSerializer.Instance.SerializerFor(typeof(FailureAcknowledgement)));
    }

    [Fact]
    public async Task routes_for_the_same_message_type_share_one_serializer()
    {
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine().StartAsync(TestContext.Current.CancellationToken);

        var runtime = host.GetRuntime();

        var first = MessageRoute.For(typeof(Acknowledgement), new LocalQueue("gh-4232-one"), runtime);
        var second = MessageRoute.For(typeof(Acknowledgement), new LocalQueue("gh-4232-two"), runtime);

        first.Serializer.ShouldBeSameAs(second.Serializer);
    }
}
