# Saga Storage

Wolverine can use registered EF Core `DbContext` types for [saga persistence](/guide/durability) as long as the EF Core transactional support
is added to the application. There's absolutely nothing you need to do to enable this except for having a mapping for 
whatever `Saga` type you need to persist in a registered `DbContext` type. As long as your `DbContext`
with a mapping for a particular `Saga` type is registered in the IoC container for your application
and Wolverine's EF Core transactional support is active, Wolverine will be able to find and use
the correct `DbContext` type for your `Saga` at runtime.

You do *not* need to use the `WolverineOptions.AddSagaType<T>()` option with EF Core saga, that option
is strictly for the [lightweight saga storage](/guide/durability/sagas.html#lightweight-saga-storage) with SQL Server or PostgreSQL. 

To make that concrete, let's say you've got a simplistic `Order` saga type like this:

<!-- snippet: sample_order_saga_for_efcore -->
<a id='snippet-sample_order_saga_for_efcore'></a>
```cs
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
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Samples/EFCoreSample/ItemService/Orders/Order.cs#L6-L79' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_order_saga_for_efcore' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

And a matching `OrdersDbContext` that can persist that type like so:

<!-- snippet: sample_OrdersDbContext -->
<a id='snippet-sample_ordersdbcontext'></a>
```cs
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
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Samples/EFCoreSample/ItemService/Orders/Order.cs#L81-L108' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_ordersdbcontext' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

There's no other registration to do other than adding the `OrdersDbContext` to your IoC container and enabling
the Wolverine EF Core middleware as shown in the [getting started with EF Core](/guide/durability/efcore/#getting-started) section. 

## When to Use EF Core vs Lightweight Storage?

As to the question, when should you opt for lightweight storage where Wolverine just sticks serialized JSON into a single 
field for a saga versus using fullblown EF Core mapping? If you have any need to *also* persist other data with a `DbContext`
service while executing any of the `Saga` steps, use EF Core mapping with that same `DbContext` type so that Wolverine can
easily manage the changes in one single transaction. If you prefer having a flat table, maybe just because it'll be easier 
to monitor through normal database tooling, use EF Core. If you just want to go fast and don't want to mess with ORM mapping,
then use the lightweight storage with Wolverine. 

Do note that using `AddSagaType<T>()` for a `Saga` type will win out over any EF Core mappings and Wolverine will try to
use the lightweight storage in that case. 
