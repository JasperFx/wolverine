using System.Reflection;
using JasperFx;
using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Model;
using JasperFx.Core.Reflection;
using Fisher;
using JasperFx.Events;
using Fisher.Events;
using Wolverine.Configuration;
using Wolverine.Persistence.EventSourcing;

namespace Wolverine.Fisher;

/// <summary>
/// Use this as a response from a message handler or HTTP endpoint using the aggregate handler workflow
/// to respond with the updated version of the aggregate being altered *after* any new events have been applied
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

    internal static bool ResolveToGuidType(Type idType)
    {
        if (idType == typeof(Guid)) return true;
        if (idType == typeof(string)) return false;

        // Check for StronglyTypedId wrapping Guid
        var valueType = ValueTypeInfo.ForType(idType);
        if (valueType != null)
        {
            return valueType.SimpleType == typeof(Guid);
        }

        // Default to Guid for unknown types
        return true;
    }
}

/// <summary>
/// Use this as a response from a message handler or HTTP endpoint using the aggregate handler workflow
/// to respond with the updated version of the aggregate being altered *after* any new events have been applied
/// </summary>
/// <typeparam name="T">The aggregate type</typeparam>
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

// GH-4765. These were generic types closed with CloseAndBuildAs over the aggregate type, which ILC
// trims -- ConfigureResponse runs from Chain.tryApplyResponseAware while the chain MODEL is built, so
// the close still fires at startup under TypeLoadMode.Static. De-genericized rather than rooted,
// because fisher#379 shipped in Fisher 1.17.0: FetchLatest is now reachable on a NON-GENERIC
// interface, which removes the instantiation instead of asking ILC to keep one. Same conversion, and
// the same reasoning, as the Marten and Polecat twins in #4793.
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
    ///     Closing the generic METHOD off a non-generic interface is AOT-safe where closing a generic TYPE
    ///     with <c>CloseAndBuildAs</c> was not — the <c>MakeGenericMethod</c> result carries
    ///     <c>ReturnType</c>, <c>GetParameters()</c> and <c>DeclaringType</c>, everything
    ///     <see cref="MethodCall" /> reads, with no direct call to that instantiation anywhere in the app.
    ///     Do not "simplify" this by constructing a closed TYPE here; that is the operation that throws
    ///     "missing native code or metadata".
    ///     <para><b>Fisher declares no <c>FetchLatest</c> of its own</b> — <c>Fisher.Events.EventOperations</c>
    ///     inherits all of them from <c>JasperFx.Events.IEventStoreOperations</c>, and
    ///     <c>GetMethods()</c> on an interface does not return members of its base interfaces, so the bases
    ///     are searched too and the results deduplicated by signature. Note also that
    ///     <c>JasperFx.Events.IEventOperations</c> is NOT a base of <c>IEventStoreOperations</c> and carries
    ///     no <c>FetchLatest</c> at all, which is why this targets the latter. Fisher already registers a
    ///     variable source for it (<c>SharedEventStoreOperationsSource</c>), so the frame resolves
    ///     <c>session.Events</c> with no new wiring.</para>
    ///     <para>The generic-arity guard matters: <c>FetchLatest</c> has a two-parameter overload
    ///     (<c>FetchLatest&lt;T1, T2&gt;(T2, CancellationToken)</c>) for strong typed identifiers, and
    ///     without the guard this lookup would be ambiguous.</para>
    /// </remarks>
    internal static MethodInfo FetchLatestMethod(Type identityType, Type aggregateType)
    {
        var open = new[] { typeof(IEventStoreOperations) }
            .Concat(typeof(IEventStoreOperations).GetInterfaces())
            .SelectMany(x => x.GetMethods())
            .Where(x => x.Name == nameof(IEventStoreOperations.FetchLatest)
                        && x.IsGenericMethodDefinition
                        && x.GetGenericArguments().Length == 1
                        && x.GetParameters()[0].ParameterType == identityType)
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
