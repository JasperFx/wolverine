using System.Reflection;
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
            var openType = idType == typeof(Guid) ? typeof(FetchLatestByGuid<>) : typeof(FetchLatestByString<>);
            var frame = openType.CloseAndBuildAs<MethodCall>(handling.AggregateId, handling.AggregateType);
            chain.UseForResponse(frame);
        }
        else
        {
            throw new InvalidOperationException($"UpdatedAggregate cannot be used because Chain {chain} is not marked as an aggregate handler.");
        }
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
            var openType = idType == typeof(Guid) ? typeof(FetchLatestByGuid<>) : typeof(FetchLatestByString<>);
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
    public FetchLatestByGuid(Variable id) : base(typeof(IEventOperations), ReflectionHelper.GetMethod<IEventOperations>(x => x.FetchLatest<T>(Guid.Empty, CancellationToken.None)))
    {
        if (id.VariableType != typeof(Guid))
        {
            throw new ArgumentOutOfRangeException(
                "Wolverine does not yet support strong typed identifiers for the aggregate workflow.");
        }

        Arguments[0] = id;
    }
}

internal class FetchLatestByString<T> : MethodCall where T : class, new()
{
    public FetchLatestByString(Variable id) : base(typeof(IEventOperations), ReflectionHelper.GetMethod<IEventOperations>(x => x.FetchLatest<T>("", CancellationToken.None)))
    {
        if (id.VariableType != typeof(string))
        {
            throw new ArgumentOutOfRangeException(
                "Wolverine does not yet support strong typed identifiers for the aggregate workflow.");
        }

        Arguments[0] = id;
    }
}
