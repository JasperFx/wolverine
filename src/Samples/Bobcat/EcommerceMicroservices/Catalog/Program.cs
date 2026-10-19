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
        ?? "Host=localhost;Port=5433;Database=catalog;Username=postgres;Password=postgres";

    opts.Connection(connectionString);
    opts.DatabaseSchemaName = "catalog";
})
.IntegrateWithWolverine()
.UseLightweightSessions();

builder.Host.UseWolverine(opts =>
{
    opts.Discovery.IncludeAssembly(typeof(Program).Assembly);
    opts.Policies.AutoApplyTransactions();
    opts.UseFluentValidation();
    opts.ServiceName = "Catalog";
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

    if (await session.Query<Catalog.Product>().AnyAsync()) return;

    session.Store(
        new Catalog.Product { Name = "IPhone X", Category = ["Smart Phone"], Description = "Apple smartphone", ImageFile = "product-1.png", Price = 950.00m },
        new Catalog.Product { Name = "Samsung 10", Category = ["Smart Phone"], Description = "Samsung smartphone", ImageFile = "product-2.png", Price = 840.00m },
        new Catalog.Product { Name = "Huawei Plus", Category = ["White Appliances"], Description = "Huawei phone", ImageFile = "product-3.png", Price = 650.00m },
        new Catalog.Product { Name = "Xiaomi Mi 9", Category = ["Smart Phone"], Description = "Xiaomi smartphone", ImageFile = "product-4.png", Price = 470.00m },
        new Catalog.Product { Name = "HTC U11+", Category = ["Smart Phone"], Description = "HTC phone", ImageFile = "product-5.png", Price = 380.00m }
    );
    await session.SaveChangesAsync();
}
