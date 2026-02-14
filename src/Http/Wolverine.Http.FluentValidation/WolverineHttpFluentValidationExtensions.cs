using Microsoft.Extensions.DependencyInjection;
using Wolverine.FluentValidation;
using Wolverine.FluentValidation.Internals;
using Wolverine.Http.FluentValidation.Internals;

namespace Wolverine.Http.FluentValidation;

public static class WolverineHttpFluentValidationExtensions
{
    /// <summary>
    ///     Apply FluentValidation middleware to message handlers that have known validators
    ///     in the underlying container
    /// </summary>
    /// <param name="discovery"></param>
    /// <returns></returns>
    public static WolverineOptions UseFluentValidation(this WolverineOptions options,
        ExtensionDiscovery discovery,
        RegistrationBehavior behavior = RegistrationBehavior.DiscoverAndRegisterValidators)
    {
        if (discovery == ExtensionDiscovery.ManualOnly)
        {
            options.Services.AddSingleton(typeof(IFailureAction<>),       typeof(FailureAction<>));
            options.Services.AddSingleton(typeof(IProblemDetailSource<>), typeof(ProblemDetailSource<>));
        }

        options.UseFluentValidation(behavior);
        return options;
    }
}