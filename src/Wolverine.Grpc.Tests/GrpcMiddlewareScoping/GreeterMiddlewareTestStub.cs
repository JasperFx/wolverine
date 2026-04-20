using Wolverine.Grpc.Tests.GrpcMiddlewareScoping.Generated;

namespace Wolverine.Grpc.Tests.GrpcMiddlewareScoping;

/// <summary>
///     Proto-first Wolverine stub for the M15 (<see cref="Wolverine.Attributes.MiddlewareScoping.Grpc"/>)
///     test suite. Intentionally bare — middleware methods carrying
///     <c>[WolverineBefore]</c>/<c>[WolverineAfter]</c> are added per-test via partial-class
///     extensions or test-specific subclasses so each scenario can scope its assertions
///     without polluting the shared stub.
/// </summary>
[WolverineGrpcService]
public abstract partial class GreeterMiddlewareTestStub : GreeterMiddlewareTest.GreeterMiddlewareTestBase;
