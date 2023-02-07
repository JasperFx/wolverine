using Microsoft.AspNetCore.Mvc;
using Wolverine;

namespace WolverineWebApi;

public class EfCoreEndpoints
{
    [HttpPost("/ef/create")]
    public void CreateItem(CreateItemCommand command, ItemsDbContext db)
    {
        db.Items.Add(new Item { Name = command.Name });
    }
    
    [HttpPost("/ef/publish")]
    public async Task PublishItem(CreateItemCommand command, ItemsDbContext db, IMessageBus bus)
    {
        var item = new Item { Name = command.Name };
        db.Items.Add(item);
        await bus.PublishAsync(new ItemCreated { Id = item.Id });
    }
}