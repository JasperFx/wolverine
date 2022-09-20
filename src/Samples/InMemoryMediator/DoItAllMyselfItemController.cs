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
        private readonly IMessageContext _messaging;
        private readonly ItemsDbContext _db;

        public DoItAllMyselfItemController(IMessageContext messaging, ItemsDbContext db)
        {
            _messaging = messaging;
            _db = db;
        }

        [HttpPost("/items/create3")]
        public async Task Create([FromBody] CreateItemCommand command)
        {
            // Start the "Outbox" transaction
            await _messaging.EnlistInOutboxAsync(_db);

            // Create a new Item entity
            var item = new Item
            {
                Name = command.Name
            };

            // Add the item to the current
            // DbContext unit of work
            _db.Items.Add(item);

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
            await _messaging.SendAsync(@event);

            // Commit the unit of work. This will persist
            // both the Item entity we created above, and
            // also a Wolverine Envelope for the outgoing
            // ItemCreated message
            await _db.SaveChangesAsync();

            // After the DbContext transaction succeeds, kick out
            // the persisted messages in the context "outbox"
            await _messaging.FlushOutgoingMessagesAsync();
        }
    }

    #endregion
}
