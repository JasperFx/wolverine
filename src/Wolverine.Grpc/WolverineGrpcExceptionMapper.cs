using Grpc.Core;

namespace Wolverine.Grpc;

/// <summary>
///     Maps common .NET exceptions to gRPC <see cref="StatusCode"/> values following
///     <see href="https://google.aip.dev/193">Google AIP-193</see> — the cross-language standard
///     for gRPC error semantics. Applied by <see cref="WolverineGrpcExceptionInterceptor"/>
///     at the gRPC adapter boundary so Wolverine handlers can throw ordinary .NET exceptions
///     and clients see idiomatic gRPC status codes without boilerplate.
/// </summary>
/// <remarks>
///     This is intentionally a small, opinionated default table. A user-configurable policy
///     and opt-in <c>google.rpc.Status</c> rich error details are planned as a later enhancement
///     — until then, unknown exceptions map to <see cref="StatusCode.Internal"/>.
/// </remarks>
public static class WolverineGrpcExceptionMapper
{
    /// <summary>
    ///     Map a .NET exception to its canonical gRPC <see cref="StatusCode"/>.
    /// </summary>
    /// <param name="exception">The exception thrown by a Wolverine handler or the gRPC adapter itself.</param>
    /// <returns>
    ///     The mapped status code, or <see cref="StatusCode.Internal"/> for unmapped exception types.
    ///     An <see cref="RpcException"/> is returned as-is (its original status code preserved).
    /// </returns>
    public static StatusCode Map(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        return exception switch
        {
            RpcException rpc => rpc.StatusCode,
            OperationCanceledException => StatusCode.Cancelled,
            TimeoutException => StatusCode.DeadlineExceeded,
            ArgumentException => StatusCode.InvalidArgument,
            KeyNotFoundException => StatusCode.NotFound,
            FileNotFoundException => StatusCode.NotFound,
            DirectoryNotFoundException => StatusCode.NotFound,
            UnauthorizedAccessException => StatusCode.PermissionDenied,
            InvalidOperationException => StatusCode.FailedPrecondition,
            NotImplementedException => StatusCode.Unimplemented,
            NotSupportedException => StatusCode.Unimplemented,
            _ => StatusCode.Internal
        };
    }

    /// <summary>
    ///     Convert an arbitrary exception into an <see cref="RpcException"/> with a mapped status code.
    ///     If <paramref name="exception"/> is already an <see cref="RpcException"/>, returns it unchanged.
    /// </summary>
    /// <param name="exception">The exception to convert.</param>
    public static RpcException ToRpcException(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        if (exception is RpcException existing)
        {
            return existing;
        }

        var status = new Status(Map(exception), exception.Message);
        return new RpcException(status);
    }
}
