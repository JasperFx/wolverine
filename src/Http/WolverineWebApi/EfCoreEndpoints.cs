using JasperFx.Core;
using Wolverine;
using Wolverine.Http;

namespace WolverineWebApi;

public interface IServiceWithDbContextReference
{
    ItemsDbContext DbContext { get; }
}

public interface IRandomService
{
    double Next();
}

public class RandomService : IRandomService
{
    private readonly Random _random = new();

    public double Next() => _random.NextDouble();
}

public class ServiceWithDbContextReference(IRandomService randomService, ItemsDbContext dbContext) : IServiceWithDbContextReference
{
    public ItemsDbContext DbContext { get; } = dbContext;
    public IRandomService RandomService { get; } = randomService;
}

public class EfCoreEndpoints
{
    [WolverinePost("/ef/create")]
    public void CreateItem(CreateItemCommand command, ItemsDbContext db)
    {
        db.Items.Add(new Item { Name = command.Name });
    }

    [WolverinePost("/ef/publish")]
    public async Task PublishItem(CreateItemCommand command, ItemsDbContext db, IMessageBus bus)
    {
        var item = new Item { Name = command.Name };
        db.Items.Add(item);
        await bus.PublishAsync(new ItemCreated { Id = item.Id });
    }

    [WolverinePost("/ef/schedule")]
    public async Task ScheduleItem(CreateItemCommand command, ItemsDbContext db, IMessageBus bus)
    {
        await bus.PublishAsync(new ItemCreated { Id = Guid.NewGuid() }, new() { ScheduleDelay = 5.Days() });
    }

    [WolverinePost("/ef/schedule2"), EmptyResponse]
    public static object ScheduleItem2(CreateItemCommand command)
    {
        return new ItemCreated { Id = Guid.NewGuid() }.DelayedFor(5.Days());
    }

    [WolverinePost("/ef/schedule_nodb")]
    public async Task ScheduleItem_NoDb(CreateItemCommand command, IMessageBus bus)
    {
        await bus.PublishAsync(new ItemCreated { Id = Guid.NewGuid() }, new() { ScheduleDelay = 5.Days() });
    }

    public static ItemsDbContext? DbContext { get; set; }
    public static ItemsDbContext? NestedDbContext { get; set; }
    [WolverinePost("/ef/servicelocation")]
    public static void Handle(CreateItemCommand command, ItemsDbContext dbContext, IServiceWithDbContextReference serviceWrapper)
    {
        DbContext = dbContext;
        NestedDbContext = serviceWrapper.DbContext;
    }
}