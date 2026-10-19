using Wolverine.FluentValidation;
using Marten;
using Wolverine;
using Wolverine.Http;
using Wolverine.Http.FluentValidation;
using Wolverine.Marten;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

builder.Services.AddWolverineHttp();

builder.Services.AddMarten(opts =>
{
    var connectionString = builder.Configuration.GetConnectionString("Marten")
        ?? "Host=localhost;Port=5433;Database=ecommerce;Username=postgres;Password=postgres";

    opts.Connection(connectionString);

    // Each module gets its own schema — clean separation within one database
    opts.Schema.For<Catalog.Product>().DatabaseSchemaName("catalog");
    opts.Schema.For<Basket.ShoppingCart>().DatabaseSchemaName("basket").Identity(x => x.Id);
    opts.Schema.For<Ordering.Order>().DatabaseSchemaName("ordering");
    opts.Schema.For<Ordering.Order>().Index(x => x.CustomerId);
    opts.Schema.For<Ordering.Order>().Index(x => x.OrderName);
    opts.Schema.For<Discount.Coupon>().DatabaseSchemaName("discount");
    opts.Schema.For<Discount.Coupon>().Index(x => x.ProductName);
})
.IntegrateWithWolverine()
.UseLightweightSessions();

builder.Host.UseWolverine(opts =>
{
    opts.Discovery.IncludeAssembly(typeof(Program).Assembly);
    opts.Policies.AutoApplyTransactions();
    opts.UseFluentValidation();
    opts.ServiceName = "EcommerceMonolith";

    // Inter-module messaging uses durable local queues —
    // messages survive process restarts via the Marten outbox
    opts.LocalQueue("basket-checkout").UseDurableInbox();
    opts.LocalQueue("order-notifications").UseDurableInbox();

    // Route BasketCheckoutEvent to the durable local queue
    opts.Publish(x =>
    {
        x.Message<Shared.BasketCheckoutEvent>();
        x.ToLocalQueue("basket-checkout");
    });
});

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.MapWolverineEndpoints(opts =>
{
    opts.UseFluentValidationProblemDetailMiddleware();
});

await SeedData(app);

await app.RunAsync();

static async Task SeedData(WebApplication app)
{
    using var scope = app.Services.CreateScope();
    var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();

    if (!await session.Query<Catalog.Product>().AnyAsync())
    {
        session.Store(
            new Catalog.Product { Name = "IPhone X", Category = ["Smart Phone"], Description = "Apple smartphone", ImageFile = "product-1.png", Price = 950.00m },
            new Catalog.Product { Name = "Samsung 10", Category = ["Smart Phone"], Description = "Samsung smartphone", ImageFile = "product-2.png", Price = 840.00m },
            new Catalog.Product { Name = "Huawei Plus", Category = ["White Appliances"], Description = "Huawei phone", ImageFile = "product-3.png", Price = 650.00m }
        );
    }

    if (!await session.Query<Discount.Coupon>().AnyAsync())
    {
        session.Store(
            new Discount.Coupon { ProductName = "IPhone X", Description = "IPhone Discount", Amount = 150 },
            new Discount.Coupon { ProductName = "Samsung 10", Description = "Samsung Discount", Amount = 100 }
        );
    }

    await session.SaveChangesAsync();
}
