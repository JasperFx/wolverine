using Wolverine.Http;

namespace SharedPersistenceModels.Items;

public record ApproveItem1(Guid Id);

public static class ApproveItem1Handler
{
    [WolverinePost("/item/approve1")]
    public static async Task Handle(ApproveItem1 command, ItemsDbContext dbContext, CancellationToken cancellationToken)
    {
        var item = await dbContext.Items.FindAsync(command.Id);
        if (item != null)
        {
            item.Approved = true;
        }
    }
}