using Microsoft.Extensions.DependencyInjection;
using Wolverine.Attributes;
using Wolverine.FluentValidation;
using Wolverine.FluentValidation.Internals;
using Wolverine.Http.FluentValidation;
using Wolverine.Http.FluentValidation.Internals;

[assembly: WolverineModule<FluentValidationExtension>]

namespace Wolverine.Http.FluentValidation;

internal class FluentValidationExtension : IWolverineExtension
{
    public void Configure(WolverineOptions options)
    {
        options.Services.AddSingleton(typeof(IFailureAction<>), typeof(FailureAction<>));

        options.Services.AddSingleton(typeof(IProblemDetailSource<>), typeof(ProblemDetailSource<>));
    }
}