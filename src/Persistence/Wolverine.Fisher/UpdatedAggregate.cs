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
            var openType = ResolveToGuidType(idType) ? typeof(FetchLatestByGuid<>) : typeof(FetchLatestByString<>);
            var frame = openType.CloseAndBuildAs<MethodCall>(handling.AggregateId, handling.AggregateType);
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

internal class FetchLatestByGuid<T> : MethodCall where T : class, new()
{
    public FetchLatestByGuid(Variable id) : base(typeof(global::Fisher.Events.EventOperations), ReflectionHelper.GetMethod<global::Fisher.Events.EventOperations>(x => x.FetchLatest<T>(Guid.Empty, CancellationToken.None))!)
    {
        Arguments[0] = UpdatedAggregateIdentity.Resolve(id, typeof(Guid));
    }
}

internal class FetchLatestByString<T> : MethodCall where T : class, new()
{
    public FetchLatestByString(Variable id) : base(typeof(global::Fisher.Events.EventOperations), ReflectionHelper.GetMethod<global::Fisher.Events.EventOperations>(x => x.FetchLatest<T>("", CancellationToken.None))!)
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
