using FluentValidation;

namespace Wolverine.FluentValidation;

/// <summary>
///     Configuration options for the Wolverine FluentValidation middleware.
///     Provides access to both Wolverine-specific registration behavior and
///     FluentValidation's global validator options.
/// </summary>
public class FluentValidationConfiguration
{
    /// <summary>
    ///     Controls whether Wolverine should auto-discover and register FluentValidation validators
    ///     or assume they are registered externally. Default is <see cref="FluentValidation.RegistrationBehavior.DiscoverAndRegisterValidators" />.
    /// </summary>
    public RegistrationBehavior RegistrationBehavior { get; set; } =
        RegistrationBehavior.DiscoverAndRegisterValidators;

    /// <summary>
    ///     Direct access to FluentValidation's global validator options for configuring
    ///     cascade modes, severity, language manager, property name resolvers, and other settings.
    /// </summary>
    /// <example>
    ///     <code>
    ///     opts.UseFluentValidation(fv =>
    ///     {
    ///         fv.ValidatorOptions.DefaultRuleLevelCascadeMode = CascadeMode.Stop;
    ///         fv.ValidatorOptions.Severity = Severity.Info;
    ///     });
    ///     </code>
    /// </example>
    public ValidatorConfiguration ValidatorOptions => global::FluentValidation.ValidatorOptions.Global;
}
