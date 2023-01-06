using Microsoft.AspNetCore.Mvc;
using Wolverine.EntityFrameworkCore;

namespace ItemService;

public class CreateItemController : ControllerBase
{
    [HttpPost("/items/create2")]
    public async Task Post(
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

        // Publish a message to take action on the new item
        // in a background thread
        await outbox.PublishAsync(new ItemCreated
        {
            Id = item.Id
        });

        // Commit all changes and flush persisted messages
        // to the persistent outbox
        // in the correct order
        await outbox.SaveChangesAndFlushMessagesAsync();
    }    
}