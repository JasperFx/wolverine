using Microsoft.Extensions.DependencyInjection;

namespace Wolverine.Http.Grpc;

/// <summary>
///     Opt-in activators for rich <c>google.rpc.Status</c> error details in
///     Wolverine-backed gRPC services. The gRPC counterpart to
///     <c>WolverineFx.Http</c>'s <c>ProblemDetails</c> flow — see
///     <c>docs/guide/http/grpc.md</c> for the user guide.
/// </summary>
public static class GrpcRichErrorDetailsExtensions
{
    /// <summary>
    ///     Opt in to rich <see cref="Google.Rpc.Status"/> error details for Wolverine-backed
    ///     gRPC services. The call is idempotent — only the first invocation wires
    ///     registrations, subsequent calls are no-ops (matching
    ///     <c>opts.UseFluentValidation()</c>'s marker pattern).
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Always registers <see cref="ValidationExceptionStatusDetailsProvider"/> so that
    ///         any DI-registered <see cref="IValidationFailureAdapter"/> (e.g.
    ///         <c>Wolverine.FluentValidation.Grpc</c>'s adapter) automatically converts
    ///         validation failures into <see cref="Google.Rpc.BadRequest"/> payloads — the
    ///         gRPC counterpart to HTTP's <c>ValidationProblemDetails</c>.
    ///     </para>
    ///     <para>
    ///         If no adapter is registered the validation provider becomes a no-op and the
    ///         interceptor falls through to the default <see cref="WolverineGrpcExceptionMapper"/>
    ///         table — there is no cost to calling this method even in hosts that don't yet
    ///         ship a validation adapter.
    ///     </para>
    /// </remarks>
    /// <param name="options">The Wolverine options being configured.</param>
    /// <param name="configure">Optional builder for inline <c>MapException</c> entries, custom
    ///     providers, and the opt-in <c>EnableDefaultErrorInfo()</c> toggle.</param>
    public static WolverineOptions UseGrpcRichErrorDetails(
        this WolverineOptions options,
        Action<GrpcRichErrorDetailsConfiguration>? configure = null)
    {
        if (options.Services.Any(x => x.ServiceType == typeof(WolverineGrpcRichDetailsMarker)))
        {
            return options;
        }

        options.Services.AddSingleton<WolverineGrpcRichDetailsMarker>();

        // Always first: validation provider. No-op if no IValidationFailureAdapter is registered.
        options.Services.AddSingleton<IGrpcStatusDetailsProvider, ValidationExceptionStatusDetailsProvider>();

        if (configure != null)
        {
            var config = new GrpcRichErrorDetailsConfiguration();
            configure(config);

            foreach (var registration in config.Registrations)
            {
                registration(options.Services);
            }

            // Always last: catch-all ErrorInfo provider, if opted in.
            if (config.DefaultErrorInfoEnabled)
            {
                options.Services.AddSingleton<IGrpcStatusDetailsProvider, DefaultErrorInfoProvider>();
            }
        }

        return options;
    }
}
