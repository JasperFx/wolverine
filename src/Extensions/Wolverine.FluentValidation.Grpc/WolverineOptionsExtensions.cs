using Microsoft.Extensions.DependencyInjection;
using Wolverine.Grpc;

namespace Wolverine.FluentValidation.Grpc;

/// <summary>
///     Activator extensions for wiring FluentValidation-produced validation failures
///     into Wolverine's gRPC rich-error-details pipeline.
/// </summary>
public static class WolverineOptionsExtensions
{
    /// <summary>
    ///     Register <see cref="FluentValidationFailureAdapter"/> so that any
    ///     <see cref="FluentValidation.ValidationException"/> thrown by a Wolverine handler
    ///     is converted into <see cref="Google.Rpc.Code.InvalidArgument"/> + a packed
    ///     <see cref="Google.Rpc.BadRequest"/> with one <c>FieldViolation</c> per failure.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Typical setup:
    ///         <code>
    /// builder.Host.UseWolverine(opts =>
    /// {
    ///     opts.UseFluentValidation();
    ///     opts.UseGrpcRichErrorDetails();
    ///     opts.UseFluentValidationGrpcErrorDetails();
    /// });
    /// builder.Services.AddWolverineGrpc();
    ///         </code>
    ///     </para>
    ///     <para>
    ///         Idempotent via a marker singleton — safe to call multiple times. Must be paired
    ///         with <c>opts.UseGrpcRichErrorDetails()</c> for the adapter to be consulted; on
    ///         its own it only places the adapter in DI, where the core interceptor's
    ///         validation provider will pick it up.
    ///     </para>
    /// </remarks>
    public static WolverineOptions UseFluentValidationGrpcErrorDetails(this WolverineOptions options)
    {
        if (options.Services.Any(x => x.ServiceType == typeof(WolverineFluentValidationGrpcMarker)))
        {
            return options;
        }

        options.Services.AddSingleton<WolverineFluentValidationGrpcMarker>();
        options.Services.AddSingleton<IValidationFailureAdapter, FluentValidationFailureAdapter>();

        return options;
    }
}

internal sealed class WolverineFluentValidationGrpcMarker
{
}
