using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Wolverine.FluentValidation;
using Wolverine.FluentValidation.Internals;
using Wolverine.Http.FluentValidation.Internals;


namespace Wolverine.Http.FluentValidation;

public static class WolverineOptionsExtensions
{
    /// <summary>
    ///     Register the services the Fluent Validation ProblemDetails middleware for Wolverine HTTP endpoints needs.
    ///     Applied automatically when this package is referenced, unless extension discovery is manual. From
    ///     Wolverine 7.0 this also applies the middleware itself whenever <c>UseFluentValidation()</c> is called
    ///     (GH-5520), unless <c>RestoreV6Defaults()</c> is called.
    /// </summary>
    public static WolverineOptions UseFluentValidationProblemDetail(this WolverineOptions options)
    {
        options.Services.TryAddSingleton(typeof(IFailureAction<>),       typeof(FailureAction<>));
        options.Services.TryAddSingleton(typeof(IProblemDetailSource<>), typeof(ProblemDetailSource<>));
        options.Services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IHttpVersionDefaults, FluentValidationHttpVersionDefaults>());

        return options;
    }
}
