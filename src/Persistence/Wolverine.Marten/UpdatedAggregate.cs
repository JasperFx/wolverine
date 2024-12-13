using System.Reflection;
using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Model;
using JasperFx.Core.Reflection;
using Marten;
using Marten.Events;
using Marten.Internal;
using Wolverine.Configuration;

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
            
            // TODO -- with https://github.com/JasperFx/wolverine/issues/1167, this might need to try to create value
            // type first
            var openType = idType == typeof(Guid) ? typeof(FetchLatestByGuid<>) : typeof(FetchLatestByString<>);
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
    public FetchLatestByGuid(Variable id) : base(typeof(IEventStore), ReflectionHelper.GetMethod<IEventStore>(x => x.FetchLatest<T>(Guid.Empty, CancellationToken.None)))
    {
        if (id.VariableType != typeof(Guid))
        {
            throw new ArgumentOutOfRangeException(
                "Wolverine does not yet support strong typed identifiers for the aggregate workflow. See https://github.com/JasperFx/wolverine/issues/1167");
        }
        
        Arguments[0] = id;
    }
}

internal class FetchLatestByString<T> : MethodCall where T : class
{
    public FetchLatestByString(Variable id) : base(typeof(IEventStore), ReflectionHelper.GetMethod<IEventStore>(x => x.FetchLatest<T>("", CancellationToken.None)))
    {
        if (id.VariableType != typeof(string))
        {
            throw new ArgumentOutOfRangeException(
                "Wolverine does not yet support strong typed identifiers for the aggregate workflow. See https://github.com/JasperFx/wolverine/issues/1167");
        }
        
        Arguments[0] = id;
    }
}