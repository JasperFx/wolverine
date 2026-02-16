using FluentValidation;
using JasperFx;
using JasperFx.Core.IoC;
using JasperFx.Core.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Wolverine.ErrorHandling;
using Wolverine.FluentValidation.Internals;

namespace Wolverine.FluentValidation;

public enum RegistrationBehavior
{
    /// <summary>
    ///     Let Wolverine discover and register the Fluent Validation validators
    /// </summary>
    DiscoverAndRegisterValidators,

    /// <summary>
    ///     Assume that the validators are registered outside of Wolverine
    /// </summary>
    ExplicitRegistration
}

public static class WolverineFluentValidationExtensions
{
    /// <summary>
    ///     Apply FluentValidation middleware to message handlers that have known validators
    ///     in the underlying container
    /// </summary>
    /// <param name="options"></param>
    /// <returns></returns>
    public static WolverineOptions UseFluentValidation(this WolverineOptions options,
        RegistrationBehavior behavior = RegistrationBehavior.DiscoverAndRegisterValidators)
    {
        if (options.Services.Any(x => x.ServiceType == typeof(WolverineFluentValidationMarker)))
        {
            return options;
        }

        options.Services.TryAddSingleton(typeof(IFailureAction<>), typeof(FailureAction<>));

        options.ConfigureLazily(o =>
        {
            if (behavior == RegistrationBehavior.DiscoverAndRegisterValidators)
            {
                if (options.ApplicationAssembly == null)
                {
                    using var provider = options.Services.BuildServiceProvider();
                    var jasperFxOptions = provider.GetService<IOptions<JasperFxOptions>>();
                    if (jasperFxOptions.Value != null)
                    {
                        options.ApplicationAssembly = jasperFxOptions.Value.ApplicationAssembly;
                    }
                    else
                    {
                        throw new InvalidOperationException(
                            "Wolverine (and JasperFx) have not been able to determine the ApplicationAssembly. Please set that explicitly");
                    }
                }
                
                options.Services.Scan(x =>
                {
                    foreach (var assembly in options.Assemblies) x.Assembly(assembly);

                    x.ConnectImplementationsToTypesClosing(typeof(IValidator<>), type => type.HasConstructorsWithArguments() ? ServiceLifetime.Scoped : ServiceLifetime.Singleton);
                });
            }
        });

        options.OnException<ValidationException>().Discard();
        options.Policies.Add<FluentValidationPolicy>();

        options.Services.AddSingleton<WolverineFluentValidationMarker>();

        return options;
    }
}