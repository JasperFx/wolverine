using Marten;
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
        if (chain.Tags.TryGetValue(nameof(AggregateHandlerAttribute.AggregateType), out var raw))
        {
            if (raw is Type aggregateType)
            {
                var method = 
            }
        }
        
        throw new InvalidOperationException($"UpdatedAggregate cannot be used because Chain {chain} is not marked as an aggregate handler. Are you missing an [AggregateHandler] or [Aggregate] attribute on the handler?");
    }
    
    public static ValueTask<T> FetchLatest<T>(IDocumentSession session)
}