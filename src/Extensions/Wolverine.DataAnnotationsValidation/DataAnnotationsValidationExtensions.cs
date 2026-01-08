using Microsoft.Extensions.DependencyInjection;
using Wolverine.DataAnnotationsValidation.Internals;
using Wolverine.ErrorHandling;

namespace Wolverine.DataAnnotationsValidation;

public static class DataAnnotationsValidationExtensions
{
    public static WolverineOptions UseDataAnnotationsValidation(this WolverineOptions options)
    {
        options.Services.AddSingleton(typeof(IFailureAction<>), typeof(FailureAction<>));
        options.OnException<ValidationException>().Discard();
        options.Policies.Add<DataAnnotationsValidationPolicy>();
        return options;
    }
}