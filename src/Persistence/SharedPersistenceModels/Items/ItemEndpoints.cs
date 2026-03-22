using Microsoft.EntityFrameworkCore;
using Wolverine.Http;
using Wolverine.Persistence;

namespace SharedPersistenceModels.Items;

public static class ItemEndpoints
{
    [WolverineGet("/item1/{id}")]
    public static async ValueTask<Item> GetItem(Guid id, ItemsDbContext dbContext) => (await dbContext.Items.FindAsync(id))!;

    [WolverineGet("/item2/{id}")]
    public static Item GetItem2(Guid id, [Entity] Item item) => item;

    [WolverineGet("/items")]
    public static async Task<Item[]> GetAll(ItemsDbContext dbContext)
    {
        var items = await dbContext.Items.ToArrayAsync();
        return items;
    }
}