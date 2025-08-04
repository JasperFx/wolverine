using JasperFx.Core.Reflection;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Wolverine;
using Wolverine.Http;
using Wolverine.Persistence;
using Wolverine.Persistence.Sagas;
using Wolverine.Runtime;

namespace SharedPersistenceModels.Orders;

public enum OrderStatus
{
    Pending = 0,
    CreditReserved = 1,
    CreditLimitExceeded = 2,
    Approved = 3,
    Rejected = 4
}

public class OrdersDbContext : DbContext
{
    public OrdersDbContext()
    {
    }

    public OrdersDbContext(DbContextOptions<OrdersDbContext> options) : base(options)
    {
    }
    
    public DbSet<Order> Orders { get; set; }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        var orders = modelBuilder.Entity<Order>();
        orders.ToTable("orders", "mt_orders");
        orders.HasKey(x => x.Id);
        orders.Property(x => x.OrderStatus);
    }
}

public record StartOrder(string Id);

public static class StartOrderHandler
{
    [WolverinePost("/orders/start"), EmptyResponse]
    public static Order Handle(StartOrder command) => new Order { Id = command.Id, OrderStatus = OrderStatus.Pending };

    [WolverineGet("/orders")]
    public static Task<Order[]> GetAllByEf(OrdersDbContext dbContext) => dbContext.Orders.ToArrayAsync();

    [WolverineGet("/orders/{id}")]
    public static Order GetOrder([Entity] Order order) => order;
}

public class Order : Saga
{
    public string? Id { get; set; }
    public OrderStatus OrderStatus { get; set; } = OrderStatus.Pending;

    public object[] Start(
        OrderPlaced orderPlaced,
        ILogger logger
    )
    {
        Id = orderPlaced.OrderId;
        logger.LogInformation("Order {OrderId} placed", Id);
        OrderStatus = OrderStatus.Pending;
        return
        [
            new ReserveCredit(
                orderPlaced.OrderId,
                orderPlaced.CustomerId,
                orderPlaced.Amount
            )
        ];
    }

    public object[] Handle(
        CreditReserved creditReserved,
        ILogger logger
    )
    {
        OrderStatus = OrderStatus.CreditReserved;
        logger.LogInformation("Credit reserver for Order {OrderId}", Id);
        return [new ApproveOrder(creditReserved.OrderId, creditReserved.CustomerId)];
    }

    public void Handle(
        OrderApproved orderApproved,
        ILogger logger
    )
    {
        OrderStatus = OrderStatus.Approved;
        logger.LogInformation("Order {OrderId} approved", Id);
    }

    public object[] Handle(
        CreditLimitExceeded creditLimitExceeded,
        ILogger logger
    )
    {
        OrderStatus = OrderStatus.CreditLimitExceeded;
        return [new RejectOrder(creditLimitExceeded.OrderId)];
    }

    public void Handle(
        OrderRejected orderRejected,
        ILogger logger
    )
    {
        OrderStatus = OrderStatus.Rejected;
        logger.LogInformation("Order {OrderId} rejected", Id);
        MarkCompleted();
    }
}