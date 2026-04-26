using Microsoft.EntityFrameworkCore;
using Wolverine.Runtime;

namespace Wolverine.EntityFrameworkCore;

/// <summary>
/// Can be used with the EF Core transactional middleware to publish domain events 
/// </summary>
public class OutgoingDomainEvents() : OutgoingMessages;

public class OutgoingDomainEventsScraper : IDomainEventScraper
{
    private readonly OutgoingDomainEvents _events;

    public OutgoingDomainEventsScraper(OutgoingDomainEvents events)
    {
        _events = events;
    }

    public Task ScrapeEvents(DbContext dbContext, MessageContext bus)
    {
        return bus.EnqueueCascadingAsync(_events);
    }
}

public interface IDomainEventScraper
{
    Task ScrapeEvents(DbContext dbContext, MessageContext bus);
}

public class DomainEventScraper<T, TEvent> : IDomainEventScraper
{
    private readonly Func<T, IEnumerable<TEvent>> _source;

    public DomainEventScraper(Func<T, IEnumerable<TEvent>> source)
    {
        _source = source;
    }

    public async Task ScrapeEvents(DbContext dbContext, MessageContext bus)
    {
        // IMPORTANT: materialize the LINQ pipeline before publishing.
        //
        // dbContext.ChangeTracker.Entries() enumerates EF's internal entity
        // state dictionary; PublishAsync flows through EfCoreEnvelopeTransaction.
        // PersistIncomingAsync, which adds an IncomingMessage entity to the
        // SAME DbContext. Mutating ChangeTracker mid-enumeration throws
        // InvalidOperationException: "Collection was modified; enumeration
        // operation may not execute." Reported in GH-2585.
        //
        // ToArray() also covers the case where _source(entity) returns a
        // live, mutable List<TEvent> that PublishAsync (e.g. via ISendMyself)
        // could mutate while we're iterating it.
        var eventMessages = dbContext.ChangeTracker.Entries().Select(x => x.Entity)
            .OfType<T>().SelectMany(_source).ToArray();

        foreach (var eventMessage in eventMessages)
        {
            await bus.PublishAsync(eventMessage);
        }
    }
}