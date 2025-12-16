using Microsoft.Extensions.DependencyInjection;
using Wolverine.Attributes;
using Wolverine.Http.DataAnnotationsValidation;
using Wolverine.Http.DataAnnotationsValidation.Internals;

[assembly: WolverineModule<DataAnnotationsValidationExtension>]

namespace Wolverine.Http.DataAnnotationsValidation;


public class DataAnnotationsValidationExtension : IWolverineExtension
{
    public void Configure(WolverineOptions options)
    {
        options.Services.AddSingleton(typeof(IProblemDetailSource<>), typeof(ProblemDetailSource<>));
    }
}