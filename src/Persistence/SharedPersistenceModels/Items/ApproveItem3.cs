using Wolverine.Http;
using Wolverine.Persistence;

namespace SharedPersistenceModels.Items;

public record ApproveItem3(Guid Id);

public static class ApproveItem3Handler
{
    [WolverinePost("/item/approve3")]
    public static IStorageAction<Item> Handle(ApproveItem3 command, [Entity(Required = false)] Item? item)
    {
        if (item != null)
        {
            item.Approved = true;
            return new Store<Item>(item);
        }

        return Storage.Nothing<Item>();
    }
}