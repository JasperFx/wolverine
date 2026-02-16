using Wolverine.Attributes;
using Wolverine.Http.FluentValidation;

[assembly: WolverineModule<FluentValidationExtension>]

namespace Wolverine.Http.FluentValidation;

internal class FluentValidationExtension : IWolverineExtension
{
    public void Configure(WolverineOptions options)
    {
        options.UseFluentValidationProblemDetail();
    }
}