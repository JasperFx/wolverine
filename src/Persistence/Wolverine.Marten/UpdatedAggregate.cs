using System.Reflection;
using JasperFx;
using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Model;
using JasperFx.Core.Reflection;
using Marten;
using Marten.Events;
using Marten.Internal;
using Wolverine.Configuration;
using Wolverine.Persistence.EventSourcing;

namespace Wolverine.Marten;

/// <summary>
/// Use this as a response from a message handler
/// or HTTP endpoint using the aggregate handler workflow
/// to response with the updated version of the aggregate being
/// altered *after* any new events have been applied
/// </summary>
public class UpdatedAggregate : IResponseAware
{
    public static void ConfigureResponse(IChain chain)
    {
        if (AggregateHandling.TryLoad(chain, out var handling))
        {
            var idType = handling.AggregateId.VariableType;

            MethodCall frame = ResolveToGuidType(idType)
                ? new FetchLatestByGuid(handling.AggregateId, handling.AggregateType)
                : new FetchLatestByString(handling.AggregateId, handling.AggregateType);

            chain.UseForResponse(frame);
        }
        else
        {
            throw new InvalidOperationException($"UpdatedAggregate cannot be used because Chain {chain} is not marked as an aggregate handler. Are you missing an [AggregateHandler] or [Aggregate] attribute on the handler?");
        }

    }

    /// <summary>
    /// Which of Marten's two <c>FetchLatest</c> overloads the response should call: the Guid one, or the
    /// string one. A strong typed identifier answers for whatever it wraps.
    /// </summary>
    /// <remarks>
    /// GH-4514: this used to be <c>idType == typeof(Guid)</c>, so any strong typed identifier fell to the
    /// string branch and then tripped a guard claiming the aggregate workflow did not support strong typed
    /// identifiers at all. The handler side has resolved them since #1167 closed, and the Polecat and Fisher
    /// ports of this class already did this -- Marten was the odd one out.
    /// </remarks>
    internal static bool ResolveToGuidType(Type idType)
    {
        if (idType == typeof(Guid)) return true;
        if (idType == typeof(string)) return false;

        var valueType = ValueTypeInfo.ForType(idType);
        if (valueType != null)
        {
            return valueType.SimpleType == typeof(Guid);
        }

        // Guid is Marten's default stream identity, so it is the better guess for an
        // unrecognized type. The MethodCall below is where an unusable type is refused.
        return true;
    }
}

/// <summary>
/// Use this as a response from a message handler
/// or HTTP endpoint using the aggregate handler workflow
/// to response with the updated version of the aggregate being
/// altered *after* any new events have been applied
/// </summary>
/// <typeparam name="T">The aggregate type. Use this version of UpdatedAggregate if you need to help Wolverine "know" which of multiple event streams should be the "updated aggregate"</typeparam>
public class UpdatedAggregate<T> : IResponseAware
{
    public static void ConfigureResponse(IChain chain)
    {
        if (AggregateHandling.TryLoad<T>(chain, out var handling))
        {
            var idType = handling.AggregateId.VariableType;

            MethodCall frame = UpdatedAggregate.ResolveToGuidType(idType)
                ? new FetchLatestByGuid(handling.AggregateId, handling.AggregateType)
                : new FetchLatestByString(handling.AggregateId, handling.AggregateType);

            chain.UseForResponse(frame);
        }
        else
        {
            throw new InvalidOperationException($"UpdatedAggregate cannot be used because Chain {chain} is not marked as an aggregate handler. Are you missing an [AggregateHandler] or [Aggregate] attribute on the handler?");
        }

    }
}

// GH-4778 / GH-4765. Not generic any more, and the aggregate type is an ordinary constructor argument.
// T was only ever used to resolve a MethodInfo for IEventStoreOperations.FetchLatest<T>, which is a type
// IDENTITY rather than a dispatch mechanism -- so this is the shape GH-4764 converted, and converting it
// removes the instantiation ILC had to be asked to keep instead of merely rooting it.
internal class FetchLatestByGuid : MethodCall
{
    public FetchLatestByGuid(Variable id, Type aggregateType)
        : base(typeof(IEventStoreOperations), UpdatedAggregateIdentity.FetchLatestMethod(typeof(Guid), aggregateType))
    {
        Arguments[0] = UpdatedAggregateIdentity.Resolve(id, typeof(Guid));
    }
}

internal class FetchLatestByString : MethodCall
{
    public FetchLatestByString(Variable id, Type aggregateType)
        : base(typeof(IEventStoreOperations), UpdatedAggregateIdentity.FetchLatestMethod(typeof(string), aggregateType))
    {
        Arguments[0] = UpdatedAggregateIdentity.Resolve(id, typeof(string));
    }
}

internal static class UpdatedAggregateIdentity
{
    /// <summary>
    ///     The closed <c>IEventStoreOperations.FetchLatest&lt;TAggregate&gt;</c> for one of the two
    ///     primitive identity overloads.
    /// </summary>
    /// <remarks>
    ///     GH-4778. Closing the generic METHOD off a non-generic interface is AOT-safe where closing a
    ///     generic TYPE with <c>CloseAndBuildAs</c> was not. Verified in a native image: the
    ///     <c>MakeGenericMethod</c> result carries <c>ReturnType</c>, <c>GetParameters()</c> and
    ///     <c>DeclaringType</c> -- everything <see cref="MethodCall" /> reads -- whether or not any direct
    ///     call to that instantiation exists in the application. Note the contrast, measured in the same
    ///     probe: <c>typeof(Task&lt;&gt;).MakeGenericType(aggregateType)</c> throws
    ///     "missing native code or metadata" unless something statically references the closed type, so do
    ///     not "simplify" this by constructing closed types here.
    ///     <para>The generic-arity guard matters: <c>FetchLatest</c> has a two-parameter overload
    ///     (<c>FetchLatest&lt;T1, T2&gt;(T2, CancellationToken)</c>) for strong typed identifiers, and
    ///     without it this lookup would be ambiguous the moment that overload's first parameter matched.</para>
    /// </remarks>
    internal static MethodInfo FetchLatestMethod(Type identityType, Type aggregateType)
    {
        // GetMethods() on an INTERFACE does not return members inherited from its base interfaces, and
        // Marten.Events.IEventStoreOperations declares no FetchLatest of its own -- it inherits all three
        // overloads from JasperFx.Events.IEventStoreOperations. Searching only the leaf interface found
        // nothing, Single() threw "Sequence contains no matching element" while the chain was being built,
        // and the handler then surfaced as NoHandlerExecutor rethrowing it with its original stack gone.
        // Hence the base interfaces are searched too.
        var open = new[] { typeof(IEventStoreOperations) }
            .Concat(typeof(IEventStoreOperations).GetInterfaces())
            .SelectMany(x => x.GetMethods())
            .Where(x => x.Name == nameof(IEventStoreOperations.FetchLatest)
                        && x.IsGenericMethodDefinition
                        && x.GetGenericArguments().Length == 1
                        && x.GetParameters()[0].ParameterType == identityType)
            // Deduplicated by signature, because the leaf interface and a base interface can BOTH declare
            // the same overload -- Polecat's IEventOperations does, and without this the search that Marten
            // needs threw "Sequence contains more than one matching element" there.
            .DistinctBy(x => x.ToString())
            .Single();

        return open.MakeGenericMethod(aggregateType);
    }

    /// <summary>
    /// The variable to pass to <c>FetchLatest</c>: the identity itself when it is already the primitive
    /// stream identity type, or the strong typed identifier's inner value when it wraps one.
    /// </summary>
    internal static Variable Resolve(Variable id, Type simpleType)
    {
        if (id.VariableType == simpleType) return id;

        var valueType = ValueTypeInfo.ForType(id.VariableType);
        if (valueType != null && valueType.SimpleType == simpleType)
        {
            return new MemberAccessVariable(id, valueType.ValueProperty);
        }

        throw new ArgumentOutOfRangeException(nameof(id),
            $"Cannot use {id.VariableType.FullNameInCode()} as the identity for UpdatedAggregate. The aggregate identity has to be a {simpleType.NameInCode()}, or a strong typed identifier wrapping a {simpleType.NameInCode()}.");
    }
}
