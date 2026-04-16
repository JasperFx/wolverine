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
    ///     in the underlying container, with full access to FluentValidation configuration.
    /// </summary>
    /// <param name="options"></param>
    /// <param name="configure">Action to configure FluentValidation behavior and validator options</param>
    /// <returns></returns>
    public static WolverineOptions UseFluentValidation(this WolverineOptions options,
        Action<FluentValidationConfiguration> configure)
    {
        var config = new FluentValidationConfiguration();
        configure(config);
        return options.UseFluentValidation(config.RegistrationBehavior, config.IncludeInternalTypes);
    }

    /// <summary>
    ///     Apply FluentValidation middleware to message handlers that have known validators
    ///     in the underlying container
    /// </summary>
    /// <param name="options"></param>
    /// <param name="behavior"></param>
    /// <param name="includeInternalTypes">When true, also discovers validators with internal visibility</param>
    /// <returns></returns>
    public static WolverineOptions UseFluentValidation(this WolverineOptions options,
        RegistrationBehavior behavior = RegistrationBehavior.DiscoverAndRegisterValidators,
        bool includeInternalTypes = false)
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
                    if (jasperFxOptions?.Value != null)
                    {
                        options.ApplicationAssembly = jasperFxOptions.Value.ApplicationAssembly;
                    }
                    else
                    {
                        throw new InvalidOperationException(
                            "Wolverine (and JasperFx) have not been able to determine the ApplicationAssembly. Please set that explicitly");
                    }
                }

                // Use FluentValidation's own AssemblyScanner when internal types are needed,
                // since Lamar's ConnectImplementationsToTypesClosing only finds public types.
                if (includeInternalTypes)
                {
                    var scanResults =
                        global::FluentValidation.AssemblyScanner.FindValidatorsInAssemblies(options.Assemblies,
                            includeInternalTypes: true);

                    foreach (var result in scanResults)
                    {
                        var lifetime = result.ValidatorType.HasConstructorsWithArguments()
                            ? ServiceLifetime.Scoped
                            : ServiceLifetime.Singleton;

                        options.Services.TryAdd(new ServiceDescriptor(result.InterfaceType, result.ValidatorType,
                            lifetime));
                    }
                }
                else
                {
                    options.Services.Scan(x =>
                    {
                        foreach (var assembly in options.Assemblies) x.Assembly(assembly);

                        x.ConnectImplementationsToTypesClosing(typeof(IValidator<>),
                            type => type.HasConstructorsWithArguments()
                                ? ServiceLifetime.Scoped
                                : ServiceLifetime.Singleton);
                    });
                }
            }
        });

        options.OnException<ValidationException>().Discard();
        options.Policies.Add<FluentValidationPolicy>();

        options.Services.AddSingleton<WolverineFluentValidationMarker>();

        return options;
    }
}