using System.Reflection;
using JasperFx;
using JasperFx.CodeGeneration;
using JasperFx.Core.Reflection;
using Wolverine.Attributes;
using Wolverine.Persistence.Sagas;

namespace Wolverine.Persistence;

/// <summary>
///     Base class for the explicit, provider-specific entity loading attributes — <c>[FromMarten]</c>,
///     <c>[FromEfCore]</c> and the rest of that family. Behaves exactly like <see cref="EntityAttribute" /> in every
///     respect except one: instead of asking every registered provider which of them claims the entity type, it
///     demands the single provider it names.
/// </summary>
/// <remarks>
///     <para>
///         Everything an <see cref="EntityAttribute" /> does — identity discovery from the message, route, header,
///         claim or method; <see cref="EntityAttribute.Required" />; the whole <see cref="OnMissing" /> matrix;
///         <see cref="EntityAttribute.MissingMessage" />; <see cref="EntityAttribute.MaybeSoftDeleted" />; the
///         deferred variable that lets <c>Before</c>/<c>After</c> middleware take the same parameter — is inherited
///         rather than reimplemented. That is deliberate: these attributes promise "the same thing as
///         <c>[Entity]</c>, but always against this store", and inheriting the one <c>Modify</c> implementation is
///         what makes that promise true by construction instead of by diligence.
///     </para>
///     <para>
///         A subclass supplies three things — which provider type to demand, what to call it in an error message,
///         and how the reader would have registered it. Everything else, including both failure diagnostics, is
///         here.
///     </para>
/// </remarks>
public abstract class ExplicitEntityAttribute : EntityAttribute
{
    protected ExplicitEntityAttribute()
    {
    }

    protected ExplicitEntityAttribute(string argumentName) : base(argumentName)
    {
    }

    /// <summary>
    ///     The <see cref="IPersistenceFrameProvider" /> implementation type this attribute demands. Matched with
    ///     <see cref="Type.IsInstanceOfType" />, so a subclassed provider still satisfies it.
    /// </summary>
    protected abstract Type providerType { get; }

    /// <summary>
    ///     Human readable name of the persistence tool for error messages, e.g. "Marten" or "EF Core".
    /// </summary>
    protected abstract string toolName { get; }

    /// <summary>
    ///     How the reader would have integrated this tool with Wolverine, quoted verbatim into the
    ///     "not registered" diagnostic.
    /// </summary>
    protected abstract string integrationCall { get; }

    /// <summary>
    ///     Why <paramref name="entityType" /> might be unknown to an otherwise-registered provider, and what to do
    ///     about it. Override for a selective provider that requires each type to be enrolled by hand — an
    ///     <c>[Entity]</c> would simply have fallen through to another provider, so this is the only place the real
    ///     cause gets said out loud.
    /// </summary>
    protected virtual string cannotPersistRemedy(Type entityType)
    {
        return $"Map {entityType.FullNameInCode()} in {toolName} to load it this way, or use a different attribute.";
    }

    protected sealed override bool tryFindProvider(GenerationRules rules, IServiceContainer container,
        ParameterInfo parameter, out IPersistenceFrameProvider provider)
    {
        var entityType = parameter.ParameterType;
        var resolution =
            rules.TryFindPersistenceFrameProviderOfType(container, providerType, entityType, out provider);

        switch (resolution)
        {
            case PersistenceProviderResolution.Found:
                return true;

            case PersistenceProviderResolution.ProviderNotRegistered:
                throw new InvalidOperationException(
                    $"{describeUsage(parameter)} explicitly requires {toolName}, but {toolName} is not integrated with this Wolverine application. Add it during bootstrapping with {integrationCall}, or use [Entity] to let Wolverine choose among the persistence providers that are registered.");

            default:
                throw new InvalidOperationException(
                    $"{describeUsage(parameter)} explicitly requires {toolName}. {toolName} is registered with this application, but it does not persist {entityType.FullNameInCode()}. {cannotPersistRemedy(entityType)}");
        }
    }

    /// <summary>
    ///     "[FromMarten] on parameter 'invoice' of My.Namespace.InvoiceHandler.Handle()" — enough for the reader to
    ///     find the declaration, which matters because these attributes fail at CODEGEN and can surface on a chain
    ///     the developer did not know was being compiled. Protected so that a provider-specific refusal in another
    ///     assembly (an <c>Include</c> path that names no navigation, say) can be reported with the same prefix as
    ///     the two failures handled here.
    /// </summary>
    protected string describeUsage(ParameterInfo parameter)
    {
        var name = GetType().Name;
        if (name.EndsWith("Attribute", StringComparison.Ordinal))
        {
            name = name.Substring(0, name.Length - "Attribute".Length);
        }

        return $"[{name}] on parameter '{parameter.Name}' of {DescribeMember(parameter)}";
    }
}
