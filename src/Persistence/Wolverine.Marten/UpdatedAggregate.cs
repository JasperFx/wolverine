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

            var openType = ResolveToGuidType(idType) ? typeof(FetchLatestByGuid<>) : typeof(FetchLatestByString<>);
            var frame = openType.CloseAndBuildAs<MethodCall>(handling.AggregateId, handling.AggregateType);

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

            var openType = UpdatedAggregate.ResolveToGuidType(idType) ? typeof(FetchLatestByGuid<>) : typeof(FetchLatestByString<>);
            var frame = openType.CloseAndBuildAs<MethodCall>(handling.AggregateId, handling.AggregateType);

            chain.UseForResponse(frame);
        }
        else
        {
            throw new InvalidOperationException($"UpdatedAggregate cannot be used because Chain {chain} is not marked as an aggregate handler. Are you missing an [AggregateHandler] or [Aggregate] attribute on the handler?");
        }

    }
}

internal class FetchLatestByGuid<T> : MethodCall where T : class
{
    public FetchLatestByGuid(Variable id) : base(typeof(IEventStoreOperations), ReflectionHelper.GetMethod<IEventStoreOperations>(x => x.FetchLatest<T>(Guid.Empty, CancellationToken.None))!)
    {
        Arguments[0] = UpdatedAggregateIdentity.Resolve(id, typeof(Guid));
    }
}

internal class FetchLatestByString<T> : MethodCall where T : class
{
    public FetchLatestByString(Variable id) : base(typeof(IEventStoreOperations), ReflectionHelper.GetMethod<IEventStoreOperations>(x => x.FetchLatest<T>("", CancellationToken.None))!)
    {
        Arguments[0] = UpdatedAggregateIdentity.Resolve(id, typeof(string));
    }
}

internal static class UpdatedAggregateIdentity
{
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
