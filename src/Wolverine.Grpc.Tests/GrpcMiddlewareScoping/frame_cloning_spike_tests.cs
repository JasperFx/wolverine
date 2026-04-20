using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Model;
using Shouldly;
using Xunit;

namespace Wolverine.Grpc.Tests.GrpcMiddlewareScoping;

/// <summary>
///     Pins the invariant Phase-1 codegen relies on: two <see cref="MethodCall"/> instances
///     built from one <see cref="System.Reflection.MethodInfo"/> do not share mutable state.
///     If the <c>_discoveredBefores</c> caching on <see cref="GrpcServiceChain"/> ever became
///     <em>instance</em> caching (e.g. storing a single <see cref="MethodCall"/> per method and
///     reusing it across every RPC override), one RPC's argument resolution would contaminate
///     its siblings during <c>GenerateCode</c>. The fix is "fresh MethodCall per emission site"
///     (see §13 of the M15 plan). This test fails loudly if that invariant is broken.
/// </summary>
public class frame_cloning_spike_tests
{
    [Fact]
    public void method_call_instances_from_one_method_info_do_not_share_mutable_state()
    {
        var methodInfo = typeof(GreeterMiddlewareTestStub)
            .GetMethod(nameof(GreeterMiddlewareTestStub.BeforeGrpc))!;

        var first = new MethodCall(typeof(GreeterMiddlewareTestStub), methodInfo);
        var second = new MethodCall(typeof(GreeterMiddlewareTestStub), methodInfo);

        // The reflection handle is immutable — sharing it is expected and safe.
        ReferenceEquals(first.Method, second.Method)
            .ShouldBeTrue("MethodInfo is an immutable reflection handle; sharing is fine");

        // The MethodCall wrappers themselves must be distinct objects.
        ReferenceEquals(first, second)
            .ShouldBeFalse("Phase-1 must instantiate a fresh MethodCall per emission site");

        // The mutable Arguments array — the one GenerateCode resolves Variables into — must be
        // a per-instance array. If this ever becomes a shared reference, resolving one emission
        // site would leak into every other emission site built from the same MethodInfo.
        ReferenceEquals(first.Arguments, second.Arguments)
            .ShouldBeFalse("Arguments[] must be per-instance — GenerateCode mutates it during codegen");

        // Mutation proof: overwrite one site's arguments and confirm the sibling is unchanged.
        // This is the concrete failure mode a caching bug would produce at runtime.
        var sentinel = Variable.For<string>("sentinel");
        first.Arguments[0] = sentinel;

        second.Arguments[0]
            .ShouldNotBe(sentinel,
                "mutating one MethodCall's Arguments must not be visible on a sibling built from the same MethodInfo");
    }

    [Fact]
    public void method_call_creates_collection_is_per_instance()
    {
        var methodInfo = typeof(GreeterMiddlewareTestStub)
            .GetMethod(nameof(GreeterMiddlewareTestStub.BeforeGrpc))!;

        var first = new MethodCall(typeof(GreeterMiddlewareTestStub), methodInfo);
        var second = new MethodCall(typeof(GreeterMiddlewareTestStub), methodInfo);

        // Creates holds output variables resolved during codegen. Like Arguments, it must be
        // a distinct collection per instance so cascading-message detection on one emission
        // doesn't leak to another.
        ReferenceEquals(first.Creates, second.Creates)
            .ShouldBeFalse("Creates collection must be per-instance");
    }
}
