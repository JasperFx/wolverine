using System.Diagnostics.CodeAnalysis;
using JasperFx.Core.Reflection;

namespace Wolverine;

public sealed partial class WolverineOptions
{
    /// <summary>
    /// Explicit (TForwarder, TForwarded) message-forwarder registrations made via
    /// <see cref="RegisterMessageForwarder{TFrom, TTo}"/> or
    /// <see cref="RegisterMessageForwarder{TForwarder}"/>. Replaces the
    /// pre-6.0 <c>Forwarders.FindForwards(Assembly)</c> assembly scan, which is
    /// now opt-in via <see cref="UseAutomaticForwarderDiscovery"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Drained at <c>HandlerGraph.Compile</c> time and merged into the
    /// runtime <see cref="Wolverine.Runtime.Serialization.Forwarders"/>
    /// instance — the on-the-wire behavior of <c>IForwardsTo&lt;T&gt;</c>
    /// dispatch (the old type forwards to the new type's handler) is
    /// unchanged from 5.x.
    /// </para>
    /// </remarks>
    internal Dictionary<Type, Type> ExplicitMessageForwarders { get; } = new();

    /// <summary>
    /// When <see langword="true"/>, <c>HandlerGraph.Compile</c> will scan
    /// <see cref="ApplicationAssembly"/>'s exported types for concrete
    /// <c>IForwardsTo&lt;T&gt;</c> implementations and register them as
    /// message forwarders — the 5.x behavior. Default in 6.0 is
    /// <see langword="false"/>; existing apps that relied on the implicit
    /// scan should either call <see cref="UseAutomaticForwarderDiscovery"/>
    /// or migrate to <see cref="RegisterMessageForwarder{TFrom, TTo}"/>
    /// (recommended; see docs/guide/migration.md).
    /// </summary>
    internal bool AutomaticForwarderDiscoveryEnabled { get; set; }

    /// <summary>
    /// Register a message forwarder so that incoming messages of type
    /// <typeparamref name="TFrom"/> are transformed into
    /// <typeparamref name="TTo"/> via <see cref="IForwardsTo{T}.Transform"/>
    /// before being dispatched to a <typeparamref name="TTo"/> handler.
    /// </summary>
    /// <typeparam name="TFrom">
    /// The source message type. Must implement <see cref="IForwardsTo{TTo}"/>.
    /// </typeparam>
    /// <typeparam name="TTo">The target message type after transformation.</typeparam>
    /// <returns>This <see cref="WolverineOptions"/> for fluent chaining.</returns>
    /// <remarks>
    /// 6.0 explicit-registration replacement for the pre-6.0
    /// <c>Forwarders.FindForwards(Assembly)</c> assembly scan. See #2757 and
    /// docs/guide/migration.md for the migration story.
    /// </remarks>
    public WolverineOptions RegisterMessageForwarder<TFrom, TTo>() where TFrom : IForwardsTo<TTo>
    {
        ExplicitMessageForwarders[typeof(TFrom)] = typeof(TTo);
        return this;
    }

    /// <summary>
    /// Register a message forwarder by inspecting the supplied type's
    /// <see cref="IForwardsTo{T}"/> closure to derive the forwarded-to type.
    /// Equivalent to calling
    /// <see cref="RegisterMessageForwarder{TFrom, TTo}"/> with the resolved
    /// pair — useful when only the source type is known statically (e.g.,
    /// generic helper code or attribute-driven registration).
    /// </summary>
    /// <typeparam name="TForwarder">
    /// A concrete type that implements <see cref="IForwardsTo{T}"/>.
    /// </typeparam>
    /// <returns>This <see cref="WolverineOptions"/> for fluent chaining.</returns>
    /// <exception cref="ArgumentException">
    /// Thrown when <typeparamref name="TForwarder"/> doesn't implement
    /// <see cref="IForwardsTo{T}"/>.
    /// </exception>
    public WolverineOptions RegisterMessageForwarder<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.Interfaces)] TForwarder>()
    {
        var forwardingInterface = typeof(TForwarder).FindInterfaceThatCloses(typeof(IForwardsTo<>));
        if (forwardingInterface is null)
        {
            throw new ArgumentException(
                $"Type {typeof(TForwarder).FullName} does not implement {typeof(IForwardsTo<>).FullName}. " +
                $"Use RegisterMessageForwarder<TFrom, TTo>() to specify both types explicitly.",
                nameof(TForwarder));
        }

        var forwardedType = forwardingInterface.GetGenericArguments().Single();
        ExplicitMessageForwarders[typeof(TForwarder)] = forwardedType;
        return this;
    }

    /// <summary>
    /// Re-enable the pre-6.0 behavior of scanning
    /// <see cref="ApplicationAssembly"/>'s exported types for concrete
    /// <see cref="IForwardsTo{T}"/> implementations at startup. Provided as
    /// a backward-compatibility escape valve while apps migrate to the
    /// explicit <see cref="RegisterMessageForwarder{TFrom, TTo}"/> API.
    /// </summary>
    /// <returns>This <see cref="WolverineOptions"/> for fluent chaining.</returns>
    /// <remarks>
    /// Marked <c>[Obsolete]</c> as a migration nudge and
    /// <c>[RequiresUnreferencedCode]</c> because the underlying assembly
    /// scan walks <see cref="System.Reflection.Assembly.ExportedTypes"/>
    /// and trimming may remove forwarder types that are only reached
    /// reflectively here. The explicit
    /// <see cref="RegisterMessageForwarder{TFrom, TTo}"/> API is AOT-clean
    /// and should be preferred for new code. Targeted for removal in 7.0.
    /// </remarks>
    [Obsolete(
        "Migrate to the explicit RegisterMessageForwarder<TFrom, TTo>() API. " +
        "UseAutomaticForwarderDiscovery() is provided as a backward-compatibility " +
        "escape valve for the 6.0 release cycle and is targeted for removal in 7.0. " +
        "See docs/guide/migration.md for the migration recipe.")]
    [RequiresUnreferencedCode(
        "UseAutomaticForwarderDiscovery() walks ApplicationAssembly.ExportedTypes for " +
        "IForwardsTo<> implementations at HandlerGraph.Compile time. Trimming may remove " +
        "forwarder types that are only reached reflectively. Migrate to " +
        "RegisterMessageForwarder<TFrom, TTo>() — see docs/guide/migration.md.")]
    public WolverineOptions UseAutomaticForwarderDiscovery()
    {
        AutomaticForwarderDiscoveryEnabled = true;
        return this;
    }
}
