using Marten;
using Wolverine;
using Wolverine.Http;
using Wolverine.Marten;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

builder.Services.AddWolverineHttp();

builder.Services.AddMarten(opts =>
{
    var connectionString = builder.Configuration.GetConnectionString("Marten")
        ?? "Host=localhost;Port=5433;Database=discount;Username=postgres;Password=postgres";

    opts.Connection(connectionString);
    opts.DatabaseSchemaName = "discount";
    opts.Schema.For<Discount.Coupon>().Index(x => x.ProductName);
})
.IntegrateWithWolverine()
.UseLightweightSessions();

builder.Host.UseWolverine(opts =>
{
    opts.Discovery.IncludeAssembly(typeof(Program).Assembly);
    opts.Policies.AutoApplyTransactions();
    opts.ServiceName = "Discount";
});

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.MapWolverineEndpoints();

await SeedData(app);

await app.RunAsync();

static async Task SeedData(WebApplication app)
{
    using var scope = app.Services.CreateScope();
    var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();

    if (await session.Query<Discount.Coupon>().AnyAsync()) return;

    session.Store(
        new Discount.Coupon { ProductName = "IPhone X", Description = "IPhone Discount", Amount = 150 },
        new Discount.Coupon { ProductName = "Samsung 10", Description = "Samsung Discount", Amount = 100 }
    );
    await session.SaveChangesAsync();
}
