using Marten;
using PaymentsMonolith;
using Wolverine;
using Wolverine.FluentValidation;
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
        ?? "Host=localhost;Port=5433;Database=inflow;Username=postgres;Password=postgres";

    opts.Connection(connectionString);

    // Schema-per-module
    opts.Schema.For<Users.User>().DatabaseSchemaName("users");
    opts.Schema.For<Customers.Customer>().DatabaseSchemaName("customers");
    opts.Schema.For<Wallets.Wallet>().DatabaseSchemaName("wallets");
    opts.Schema.For<Wallets.Wallet>().Index(x => x.OwnerId);
    opts.Schema.For<Payments.Deposit>().DatabaseSchemaName("payments");
    opts.Schema.For<Payments.Deposit>().Index(x => x.CustomerId);
})
.IntegrateWithWolverine()
.UseLightweightSessions();

builder.Host.UseWolverine(opts =>
{
    opts.Discovery.IncludeAssembly(typeof(Program).Assembly);
    opts.Policies.AutoApplyTransactions();
    opts.UseFluentValidation();
    opts.ServiceName = "Inflow";

    // Durable local queues for inter-module messaging
    opts.LocalQueue("users").UseDurableInbox();
    opts.LocalQueue("customers").UseDurableInbox();
    opts.LocalQueue("wallets").UseDurableInbox();
    opts.LocalQueue("payments").UseDurableInbox();

    // Route integration events to module-specific queues
    opts.Publish(x => { x.Message<UserCreated>(); x.ToLocalQueue("customers"); });
    opts.Publish(x => { x.Message<CustomerCompleted>(); x.ToLocalQueue("wallets"); });
    opts.Publish(x => { x.Message<WalletCreated>(); x.ToLocalQueue("payments"); });
    opts.Publish(x => { x.Message<FundsAdded>(); x.ToLocalQueue("payments"); });
    opts.Publish(x => { x.Message<FundsDeducted>(); x.ToLocalQueue("payments"); });
    opts.Publish(x => { x.Message<DepositCompleted>(); x.ToLocalQueue("wallets"); });
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
