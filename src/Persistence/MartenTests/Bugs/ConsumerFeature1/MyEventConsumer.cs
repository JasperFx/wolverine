using Marten;
using Microsoft.Extensions.Logging;
using Wolverine.Marten;

namespace MartenTests.Bugs.ConsumerFeature1;

public class MyEventConsumer
{
    public DomainObjectX LoadAsync(IDocumentSession session) =>
        // force to write the same document which has an opt-in for optimistic concurrency
        session.Query<DomainObjectX>().First();
    
    public static IMartenOp Consume(MyEvent @event, DomainObjectX domainObjectX, ILogger<MyEventConsumer> logger)
    {
        logger.LogInformation("consumer 1 processing: {Id}", @event.Id);
        
        return MartenOps.Update(domainObjectX);
    }
}