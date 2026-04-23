using Grpc.Core;
using Wolverine.Grpc.Tests.GrpcValidateConvention.Generated;

namespace Wolverine.Grpc.Tests.GrpcValidateConvention;

/// <summary>
///     Proto-first stub for the Validate convention tests. The static <c>Validate</c> method
///     exercises the <c>Status?</c> short-circuit path that M15 Phase 2 weaves into the
///     generated gRPC service wrapper.
/// </summary>
[WolverineGrpcService]
public abstract class ValidateGreeterStub : ValidatorGreeterTest.ValidatorGreeterTestBase
{
    /// <summary>
    ///     Returns a non-null <see cref="Status"/> to reject the call before the Wolverine
    ///     handler runs. Returning <c>null</c> lets execution continue normally.
    /// </summary>
    public static Status? Validate(ValidateGreetRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Name))
            return new Status(StatusCode.InvalidArgument, "Name is required");

        if (request.Name.StartsWith("forbidden:", StringComparison.OrdinalIgnoreCase))
            return new Status(StatusCode.PermissionDenied, "Name prefix is not allowed");

        return null;
    }
}
