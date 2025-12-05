using Microsoft.EntityFrameworkCore;
using Wolverine.Runtime;

namespace Wolverine.EntityFrameworkCore;

/// <summary>
/// Can be used with the EF Core transactional middleware to publish domain events 
/// </summary>
public class DomainEvents() : OutgoingMessages;

public class DomainEventsScraper : IDomainEventScraper
{
    private readonly DomainEvents _events;

    public DomainEventsScraper(DomainEvents events)
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
        var eventMessages = dbContext.ChangeTracker.Entries().Select(x => x.Entity)
            .OfType<T>().SelectMany(_source);

        foreach (var eventMessage in eventMessages)
        {
            await bus.PublishAsync(eventMessage);
        }
    }
}