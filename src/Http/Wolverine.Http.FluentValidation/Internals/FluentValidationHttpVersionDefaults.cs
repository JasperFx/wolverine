using Microsoft.Extensions.DependencyInjection;
using Wolverine.FluentValidation.Internals;

namespace Wolverine.Http.FluentValidation.Internals;

/// <summary>
/// GH-5520. From Wolverine 7.0, an application that references WolverineFx.Http.FluentValidation and calls
/// <c>UseFluentValidation()</c> gets the HTTP ProblemDetails middleware without also calling
/// <see cref="WolverineHttpOptionsExtensions.UseFluentValidationProblemDetailMiddleware" />.
/// <c>RestoreV6Defaults()</c> and <see cref="WolverineHttpOptionsExtensions.DoNotUseFluentValidationProblemDetailMiddleware" />
/// each leave it off, and an explicit <c>UseFluentValidationProblemDetailMiddleware()</c> wins over both.
/// </summary>
internal class FluentValidationHttpVersionDefaults : IHttpVersionDefaults
{
    public void Apply(WolverineHttpOptions options, WolverineOptions wolverineOptions, IServiceProvider services)
    {
        if (wolverineOptions.V6DefaultsRestored) return;
        if (options.Policies.OfType<FluentValidationProblemDetailMiddlewareOptOut>().Any()) return;

        // UseFluentValidation() registers the marker; without it there are no validators to apply
        if (services.GetService<WolverineFluentValidationMarker>() == null) return;

        options.UseFluentValidationProblemDetailMiddleware();
    }
}

/// <summary>
/// Placed in <see cref="WolverineHttpOptions.Policies" /> by
/// <see cref="WolverineHttpOptionsExtensions.DoNotUseFluentValidationProblemDetailMiddleware" /> to record the
/// opt-out. Applies nothing.
/// </summary>
internal class FluentValidationProblemDetailMiddlewareOptOut : IHttpPolicy
{
    public void Apply(IReadOnlyList<HttpChain> chains, JasperFx.CodeGeneration.GenerationRules rules,
        JasperFx.IServiceContainer container)
    {
    }
}
