using JasperFx.Core;
using Microsoft.AspNetCore.Mvc;
using Wolverine.Attributes;
using Wolverine.EntityFrameworkCore;
using Wolverine.Http;
using Wolverine.Persistence;

namespace ItemService;

public class CreateItemController : ControllerBase
{
    #region sample_using_dbcontext_outbox_1

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

    #endregion


    #region sample_using_dbcontext_outbox_2

    [HttpPost("/items/create3")]
    public async Task Post3(
        [FromBody] CreateItemCommand command,
        [FromServices] ItemsDbContext dbContext,
        [FromServices] IDbContextOutbox outbox)
    {
        // Create a new Item entity
        var item = new Item
        {
            Name = command.Name
        };

        // Add the item to the current
        // DbContext unit of work
        dbContext.Items.Add(item);

        // Gotta attach the DbContext to the outbox
        // BEFORE sending any messages
        outbox.Enroll(dbContext);

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

    #endregion
}

public static class CreateItemEndpoint
{
    [Transactional]
    [WolverinePost("/items/create4"), EmptyResponse]
    public static (ItemCreated, Insert<Item>) Post(CreateItemCommand command)
    {
        // Create a new Item entity
        var item = new Item
        {
            Name = command.Name,
            Id = CombGuidIdGeneration.NewGuid()
        };

        return (new ItemCreated { Id = item.Id }, Storage.Insert(item));
    }

    [WolverineGet("/api/item/{id}")]
    public static Item Get([Entity] Item item) => item;
}