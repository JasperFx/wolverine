using Humanizer;
using Wolverine;
using Wolverine.Http;
using Wolverine.Persistence;

namespace SharedPersistenceModels.Items;

public record StartNewItem(Guid Id, string Name);

public static class StartNewItemHandler
{
    [WolverinePost("/item")]
    public static IStorageAction<Item> Handle(StartNewItem command)
    {
        return new Insert<Item>(new Item
        {
            Id = command.Id, 
            Name = command.Name
        });
    }
}

public record StartAndTriggerApproval(Guid Id, string Name);
public record StartAndScheduleApproval(Guid Id, string Name);

public static class StartAndTriggerApprovalHandler
{
    public static (IStorageAction<Item>, ApproveItem1) Handle(StartAndTriggerApproval command)
    {
        var storageAction = Storage.Insert(new Item { Id = command.Id, Name = command.Name });
        return (storageAction, new ApproveItem1(command.Id));
    }
    
    public static (IStorageAction<Item>, object) Handle(StartAndScheduleApproval command)
    {
        var storageAction = Storage.Insert(new Item { Id = command.Id, Name = command.Name });
        return (storageAction, new ApproveItem1(command.Id).DelayedFor(1.Hours()));
    }
}