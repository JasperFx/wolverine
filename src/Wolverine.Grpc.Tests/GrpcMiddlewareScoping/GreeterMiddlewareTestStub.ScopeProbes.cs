using Wolverine.Attributes;

namespace Wolverine.Grpc.Tests.GrpcMiddlewareScoping;

/// <summary>
///     Probe methods used by <c>scope_discovery_tests</c> to verify that
///     <see cref="GrpcServiceChain.DiscoveredBefores"/> / <see cref="GrpcServiceChain.DiscoveredAfters"/>
///     respect <see cref="MiddlewareScoping"/>. Lives on the smoke stub via <c>partial</c> so tests
///     don't need a second proto to exercise the discovery path. These are inert under Phase 0
///     (no weaving yet); when Phase 1 lands, they'll be the first concrete demonstration that the
///     M15 promise (middleware fires alongside the gRPC handler) actually holds.
/// </summary>
public abstract partial class GreeterMiddlewareTestStub
{
    public const string AnywhereMarker = "ScopeProbe.Anywhere";
    public const string GrpcMarker = "ScopeProbe.Grpc";
    public const string MessageHandlersMarker = "ScopeProbe.MessageHandlers";

    [WolverineBefore]
    public static void BeforeAnywhere(MiddlewareInvocationSink sink) => sink.Record(AnywhereMarker);

    [WolverineBefore(MiddlewareScoping.Grpc)]
    public static void BeforeGrpc(MiddlewareInvocationSink sink) => sink.Record(GrpcMarker);

    [WolverineBefore(MiddlewareScoping.MessageHandlers)]
    public static void BeforeMessageHandlers(MiddlewareInvocationSink sink) => sink.Record(MessageHandlersMarker);

    [WolverineAfter]
    public static void AfterAnywhere(MiddlewareInvocationSink sink) => sink.Record(AnywhereMarker + ".After");

    [WolverineAfter(MiddlewareScoping.Grpc)]
    public static void AfterGrpc(MiddlewareInvocationSink sink) => sink.Record(GrpcMarker + ".After");

    [WolverineAfter(MiddlewareScoping.MessageHandlers)]
    public static void AfterMessageHandlers(MiddlewareInvocationSink sink) => sink.Record(MessageHandlersMarker + ".After");
}
