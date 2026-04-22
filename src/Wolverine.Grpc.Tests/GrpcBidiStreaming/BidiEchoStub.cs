using Wolverine.Grpc.Tests.GrpcBidiStreaming.Generated;

namespace Wolverine.Grpc.Tests.GrpcBidiStreaming;

/// <summary>
///     Proto-first stub for the bidirectional-streaming tests. Carries no extra methods —
///     the generated wrapper's <c>Echo</c> override is produced entirely by Wolverine codegen.
/// </summary>
[WolverineGrpcService]
public abstract class BidiEchoStub : BidiEchoTest.BidiEchoTestBase;
