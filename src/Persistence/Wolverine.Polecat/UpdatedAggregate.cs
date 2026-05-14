using System.Reflection;
using JasperFx;
using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Model;
using JasperFx.Core.Reflection;
using Polecat;
using JasperFx.Events;
using Polecat.Events;
using Wolverine.Configuration;

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
            var openType = ResolveToGuidType(idType) ? typeof(FetchLatestByGuid<>) : typeof(FetchLatestByString<>);
            var frame = openType.CloseAndBuildAs<MethodCall>(handling.AggregateId, handling.AggregateType);
            chain.UseForResponse(frame);
        }
        else
        {
            throw new InvalidOperationException($"UpdatedAggregate cannot be used because Chain {chain} is not marked as an aggregate handler.");
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
            throw new InvalidOperationException($"UpdatedAggregate cannot be used because Chain {chain} is not marked as an aggregate handler.");
        }
    }
}

internal class FetchLatestByGuid<T> : MethodCall where T : class, new()
{
    public FetchLatestByGuid(Variable id) : base(typeof(global::Polecat.Events.IEventOperations), ReflectionHelper.GetMethod<global::Polecat.Events.IEventOperations>(x => x.FetchLatest<T>(Guid.Empty, CancellationToken.None))!)
    {
        var resolvedId = id;
        if (id.VariableType != typeof(Guid))
        {
            // Try to unwrap StronglyTypedId to its underlying Guid value
            var valueType = ValueTypeInfo.ForType(id.VariableType);
            if (valueType != null && valueType.SimpleType == typeof(Guid))
            {
                resolvedId = new MemberAccessVariable(id, valueType.ValueProperty);
            }
            else
            {
                throw new ArgumentOutOfRangeException(
                    $"Cannot use type {id.VariableType.FullNameInCode()} as a Guid aggregate identity.");
            }
        }

        Arguments[0] = resolvedId;
    }
}

internal class FetchLatestByString<T> : MethodCall where T : class, new()
{
    public FetchLatestByString(Variable id) : base(typeof(global::Polecat.Events.IEventOperations), ReflectionHelper.GetMethod<global::Polecat.Events.IEventOperations>(x => x.FetchLatest<T>("", CancellationToken.None))!)
    {
        var resolvedId = id;
        if (id.VariableType != typeof(string))
        {
            // Try to unwrap StronglyTypedId to its underlying string value
            var valueType = ValueTypeInfo.ForType(id.VariableType);
            if (valueType != null && valueType.SimpleType == typeof(string))
            {
                resolvedId = new MemberAccessVariable(id, valueType.ValueProperty);
            }
            else
            {
                throw new ArgumentOutOfRangeException(
                    $"Cannot use type {id.VariableType.FullNameInCode()} as a string aggregate identity.");
            }
        }

        Arguments[0] = resolvedId;
    }
}
