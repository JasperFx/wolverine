using Microsoft.EntityFrameworkCore;
using Wolverine;

namespace ItemService.Orders;

#region sample_order_saga_for_efcore

public enum OrderStatus
{
    Pending = 0,
    CreditReserved = 1,
    CreditLimitExceeded = 2,
    Approved = 3,
    Rejected = 4
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

#endregion

#region sample_OrdersDbContext

public class OrdersDbContext : DbContext
{
    protected OrdersDbContext()
    {
    }

    public OrdersDbContext(DbContextOptions options) : base(options)
    {
    }
    
    public DbSet<Order> Orders { get; set; }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        // Your normal EF Core mapping
        modelBuilder.Entity<Order>(map =>
        {
            map.ToTable("orders", "sample");
            map.HasKey(x => x.Id);
            map.Property(x => x.OrderStatus)
                .HasConversion(v => v.ToString(), v => Enum.Parse<OrderStatus>(v));
        });
    }
}

#endregion