using Wolverine.Http;

namespace SharedPersistenceModels.Items;

public record ApproveItem2(Guid Id);

public static class ApproveItem2Handler
{
    public static ValueTask<Item?> LoadAsync(ApproveItem2 command, ItemsDbContext dbContext,
        CancellationToken cancellationToken)
    {
        return dbContext.Items.FindAsync(command.Id, cancellationToken);
    }
    
    [WolverinePost("/item/approve2")]
    public static void Handle(ApproveItem2 _, Item item)
    {
        item.Approved = true;
    }
}