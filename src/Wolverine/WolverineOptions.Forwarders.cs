using System.Diagnostics.CodeAnalysis;
using JasperFx.Core.Reflection;

namespace Wolverine;

public sealed partial class WolverineOptions
{
    /// <summary>
    /// Explicit (TForwarder, TForwarded) message-forwarder registrations made via
    /// <see cref="RegisterMessageForwarder{TFrom, TTo}"/> or
    /// <see cref="RegisterMessageForwarder{TForwarder}"/>. Replaces the
    /// pre-6.0 <c>Forwarders.FindForwards(Assembly)</c> assembly scan. The
    /// <c>UseAutomaticForwarderDiscovery()</c> opt-in back into that scan was
    /// removed in 7.0 (GH-5198).
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
}
