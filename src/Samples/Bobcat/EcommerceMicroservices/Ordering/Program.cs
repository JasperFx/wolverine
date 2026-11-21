using JasperFx;
using JasperFx.CodeGeneration;
using Wolverine.FluentValidation;
using Marten;
using Wolverine;
using Wolverine.Http;
using Wolverine.Http.FluentValidation;
using Wolverine.Marten;
using Wolverine.RabbitMQ;

var builder = WebApplication.CreateBuilder(args);

// GH-5703: a plain `dotnet run` of this sample has no launch settings, so it runs in Production, where Wolverine 7.0
// expects pre-generated code. A sample compiles its generated code at runtime in every environment instead. Dynamic
// rather than Auto, which would write the generated code into the project
builder.Services.CritterStackDefaults(x => x.Production.GeneratedCodeMode = TypeLoadMode.Dynamic);

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

builder.Services.AddWolverineHttp();

builder.Services.AddMarten(opts =>
{
    var connectionString = builder.Configuration.GetConnectionString("Marten")
        ?? "Host=localhost;Port=5433;Database=ordering;Username=postgres;Password=postgres";

    opts.Connection(connectionString);
    opts.DatabaseSchemaName = "ordering";

    opts.Schema.For<Ordering.Order>().Index(x => x.CustomerId);
    opts.Schema.For<Ordering.Order>().Index(x => x.OrderName);
})
.IntegrateWithWolverine()
.UseLightweightSessions();

builder.Host.UseWolverine(opts =>
{
    opts.Discovery.IncludeAssembly(typeof(Program).Assembly);
    opts.Policies.AutoApplyTransactions();
    opts.UseFluentValidation();
    opts.ServiceName = "Ordering";

    // Listen for BasketCheckoutEvent from the Basket service via RabbitMQ
    opts.UseRabbitMq(rabbit =>
    {
        rabbit.HostName = builder.Configuration["RabbitMQ:Host"] ?? "localhost";
    }).AutoProvision();

    opts.ListenToRabbitQueue("basket-checkout");
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

await app.RunAsync();
