using Microsoft.Extensions.DependencyInjection.Extensions;
using Wolverine.FluentValidation;
using Wolverine.FluentValidation.Internals;
using Wolverine.Http.FluentValidation.Internals;


namespace Wolverine.Http.FluentValidation;

public static class WolverineOptionsExtensions
{
    public static WolverineOptions UseFluentValidationProblemDetail(this WolverineOptions options)
    {
        options.Services.TryAddSingleton(typeof(IFailureAction<>),       typeof(FailureAction<>));
        options.Services.TryAddSingleton(typeof(IProblemDetailSource<>), typeof(ProblemDetailSource<>));

        return options;
    }
}