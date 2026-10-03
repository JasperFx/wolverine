using System.Reflection;
using JasperFx;
using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Model;
using JasperFx.Core.Reflection;
using Polecat;
using JasperFx.Events;
using Polecat.Events;
using Wolverine.Configuration;
using Wolverine.Persistence.EventSourcing;

namespace Wolverine.Polecat;

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

// GH-4778 / GH-4765. Not generic any more: T was only used to resolve a MethodInfo for
// IEventOperations.FetchLatest<T>, a type IDENTITY rather than a dispatch mechanism, so the aggregate type
// is an ordinary constructor argument and there is no instantiation for ILC to trim. Safe here because the
// declaring type is an INTERFACE -- measured in a native image, where the same shape on a concrete class is
// not (which is why Fisher's copy is unconverted; see JasperFx/fisher#379).
internal class FetchLatestByGuid : MethodCall
{
    public FetchLatestByGuid(Variable id, Type aggregateType)
        : base(typeof(global::Polecat.Events.IEventOperations),
            UpdatedAggregateIdentity.FetchLatestMethod(typeof(Guid), aggregateType))
    {
        Arguments[0] = UpdatedAggregateIdentity.Resolve(id, typeof(Guid));
    }
}

internal class FetchLatestByString : MethodCall
{
    public FetchLatestByString(Variable id, Type aggregateType)
        : base(typeof(global::Polecat.Events.IEventOperations),
            UpdatedAggregateIdentity.FetchLatestMethod(typeof(string), aggregateType))
    {
        Arguments[0] = UpdatedAggregateIdentity.Resolve(id, typeof(string));
    }
}

internal static class UpdatedAggregateIdentity
{
    /// <summary>
    ///     The closed <c>IEventOperations.FetchLatest&lt;TAggregate&gt;</c> for one of the two primitive
    ///     identity overloads.
    /// </summary>
    /// <remarks>
    ///     GH-4778. Base interfaces are searched as well as the leaf: <c>GetMethods()</c> on an interface
    ///     does not return members inherited from its base interfaces. Polecat's
    ///     <c>IEventOperations</c> happens to declare both overloads itself, but Marten's equivalent does
    ///     not, and searching only the leaf there broke <c>UpdatedAggregate</c> outright with a
    ///     "Sequence contains no matching element" raised during chain building. The arity guard matters for
    ///     the same class of reason -- a future two-generic-parameter overload would make this ambiguous.
    /// </remarks>
    internal static MethodInfo FetchLatestMethod(Type identityType, Type aggregateType)
    {
        var open = new[] { typeof(global::Polecat.Events.IEventOperations) }
            .Concat(typeof(global::Polecat.Events.IEventOperations).GetInterfaces())
            .SelectMany(x => x.GetMethods())
            .Where(x => x.Name == nameof(global::Polecat.Events.IEventOperations.FetchLatest)
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
