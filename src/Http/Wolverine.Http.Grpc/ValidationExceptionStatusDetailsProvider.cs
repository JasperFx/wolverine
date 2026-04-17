using Google.Protobuf.WellKnownTypes;
using Google.Rpc;
using Grpc.Core;
using Status = Google.Rpc.Status;

namespace Wolverine.Http.Grpc;

/// <summary>
///     Built-in <see cref="IGrpcStatusDetailsProvider"/> that converts a validation exception
///     into <see cref="Code.InvalidArgument"/> plus a packed <see cref="BadRequest"/> containing
///     one <see cref="BadRequest.Types.FieldViolation"/> per failed rule — the gRPC counterpart
///     to <see cref="WolverineFx.Http"/>'s <c>ValidationProblemDetails</c> (HTTP 400).
/// </summary>
/// <remarks>
///     Delegates exception recognition to DI-registered <see cref="IValidationFailureAdapter"/>s.
///     Registered automatically when <c>opts.UseGrpcRichErrorDetails(...)</c> is called; becomes
///     a no-op (returns <c>null</c>) when no adapter claims the exception, so the interceptor
///     falls through to the next provider or to the default mapping table.
/// </remarks>
public sealed class ValidationExceptionStatusDetailsProvider : IGrpcStatusDetailsProvider
{
    private readonly IEnumerable<IValidationFailureAdapter> _adapters;

    public ValidationExceptionStatusDetailsProvider(IEnumerable<IValidationFailureAdapter> adapters)
    {
        _adapters = adapters;
    }

    public Status? BuildStatus(Exception exception, ServerCallContext context)
    {
        foreach (var adapter in _adapters)
        {
            if (!adapter.CanHandle(exception)) continue;

            var badRequest = new BadRequest();
            badRequest.FieldViolations.AddRange(adapter.ToFieldViolations(exception));

            return new Status
            {
                Code = (int)Code.InvalidArgument,
                Message = "Validation failed",
                Details = { Any.Pack(badRequest) }
            };
        }

        return null;
    }
}
