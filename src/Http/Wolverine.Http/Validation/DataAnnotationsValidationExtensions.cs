using Microsoft.Extensions.DependencyInjection;
using Wolverine.Attributes;
using Wolverine.Http.Validation;
using Wolverine.Http.Validation.Internals;

[assembly: WolverineModule<DataAnnotationsValidationExtension>]

namespace Wolverine.Http.Validation;


public class DataAnnotationsValidationExtension : IWolverineExtension
{
    public void Configure(WolverineOptions options)
    {
        options.Services.AddSingleton(typeof(IProblemDetailSource<>), typeof(ProblemDetailSource<>));
    }
}