using Google.Protobuf.WellKnownTypes;
using Google.Rpc;
using Grpc.Core;
using Status = Google.Rpc.Status;

namespace Wolverine.Grpc;

/// <summary>
///     Catch-all <see cref="IGrpcStatusDetailsProvider"/> that assigns <see cref="Code.Internal"/>
///     plus a minimal <see cref="ErrorInfo"/> payload to any exception not already handled by an
///     earlier provider. Opt-in via <c>cfg.EnableDefaultErrorInfo()</c>; off by default.
/// </summary>
/// <remarks>
///     <para>
///         The emitted <see cref="ErrorInfo"/> carries only <c>Reason = exception.GetType().Name</c>
///         and <c>Domain = "wolverine.grpc"</c>. It deliberately omits the exception message,
///         stack trace, and inner-exception text so it is safe to enable in production — clients
///         receive a stable machine-readable reason code without leaking server internals.
///     </para>
///     <para>
///         Because this provider always returns a non-null status when registered, it should be
///         registered last in the provider chain. <see cref="GrpcRichErrorDetailsConfiguration"/>
///         handles ordering automatically.
///     </para>
/// </remarks>
public sealed class DefaultErrorInfoProvider : IGrpcStatusDetailsProvider
{
    /// <summary>
    ///     Fixed <see cref="ErrorInfo.Domain"/> applied to every emitted payload.
    /// </summary>
    public const string Domain = "wolverine.grpc";

    public Status BuildStatus(Exception exception, ServerCallContext context)
    {
        var errorInfo = new ErrorInfo
        {
            Reason = exception.GetType().Name,
            Domain = Domain
        };

        return new Status
        {
            Code = (int)Code.Internal,
            Message = "Internal server error",
            Details = { Any.Pack(errorInfo) }
        };
    }

    Status? IGrpcStatusDetailsProvider.BuildStatus(Exception exception, ServerCallContext context)
        => BuildStatus(exception, context);
}
