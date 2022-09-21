using Wolverine;
using Wolverine.EntityFrameworkCore;
using Microsoft.AspNetCore.Mvc;

namespace InMemoryMediator
{
    #region sample_InMemoryMediator_DoItAllMyselfItemController

    // This controller does all the transactional work and business
    // logic all by itself
    public class DoItAllMyselfItemController : ControllerBase
    {
        [HttpPost("/items/create3")]
        public async Task Create(
            [FromBody] CreateItemCommand command,
            [FromServices] IDbContextOutbox<ItemsDbContext> outbox)
        {
            // Create a new Item entity
            var item = new Item
            {
                Name = command.Name
            };

            // Add the item to the current
            // DbContext unit of work
            outbox.DbContext.Items.Add(item);

            // Publish an event to anyone
            // who cares that a new Item has
            // been created
            var @event = new ItemCreated
            {
                Id = item.Id
            };

            // Because the message context is enlisted in an
            // "outbox" transaction, these outgoing messages are
            // held until the ongoing transaction completes
            await outbox.SendAsync(@event);

            // Commit the unit of work. This will persist
            // both the Item entity we created above, and
            // also a Wolverine Envelope for the outgoing
            // ItemCreated message
            await outbox.SaveChangesAndFlushMessagesAsync();
        }
    }

    #endregion
}
