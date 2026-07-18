using Wolverine.Grpc.Tests.GrpcClientStreaming.Generated;

namespace Wolverine.Grpc.Tests.GrpcClientStreaming;

/// <summary>
///     Proto-first stub for the client-streaming tests. Carries no extra methods —
///     the generated wrapper's <c>Collect</c> override is produced entirely by Wolverine codegen.
/// </summary>
[WolverineGrpcService]
public abstract class CollectStub : CollectTest.CollectTestBase;
