using Google.Rpc;
using Status = Google.Rpc.Status;

namespace Wolverine.Http.Grpc;

/// <summary>
///     Adapter that converts a specific validation-exception type into a sequence of
///     <see cref="BadRequest.Types.FieldViolation"/>s for packing into the gRPC
///     <see cref="Status"/> trailer. One adapter exists per Wolverine validation extension
///     (FluentValidation, DataAnnotations) and ships from that extension's own package —
///     core <c>Wolverine.Http.Grpc</c> deliberately avoids depending on any validation
///     library.
/// </summary>
/// <remarks>
///     <para>
///         The built-in <see cref="ValidationExceptionStatusDetailsProvider"/> iterates
///         DI-registered adapters in registration order and short-circuits on the first
///         <see cref="CanHandle"/> match. No reflection is used — each adapter is statically
///         bound to its exception type.
///     </para>
/// </remarks>
public interface IValidationFailureAdapter
{
    /// <summary>
    ///     Returns <c>true</c> if this adapter recognises <paramref name="exception"/> as
    ///     a validation failure it can translate.
    /// </summary>
    bool CanHandle(Exception exception);

    /// <summary>
    ///     Translate <paramref name="exception"/> into one <see cref="BadRequest.Types.FieldViolation"/>
    ///     per underlying failure. Only called when <see cref="CanHandle"/> returned <c>true</c>
    ///     for the same exception.
    /// </summary>
    IEnumerable<BadRequest.Types.FieldViolation> ToFieldViolations(Exception exception);
}
