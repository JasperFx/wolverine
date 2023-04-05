using IntegrationTests;
using Marten;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Oakton;
using Oakton.Resources;
using Wolverine;
using Wolverine.EntityFrameworkCore;
using Wolverine.FluentValidation;
using Wolverine.Http;
using Wolverine.Http.FluentValidation;
using Wolverine.Marten;
using WolverineWebApi;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.

// Learn more about configuring Swagger/OpenAPI at https://aka.ms/aspnetcore/swashbuckle
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

builder.Services.AddAuthorization();

builder.Services.AddDbContextWithWolverineIntegration<ItemsDbContext>(
    x => x.UseNpgsql(Servers.PostgresConnectionString));


builder.Services.AddMarten(opts =>
{
    opts.Connection(Servers.PostgresConnectionString);
    opts.DatabaseSchemaName = "http";
}).IntegrateWithWolverine();


builder.Services.AddResourceSetupOnStartup();

builder.Services.AddSingleton<Recorder>();

// Need this.
builder.Host.UseWolverine(opts =>
{
    // Set up Entity Framework Core as the support
    // for Wolverine's transactional middleware
    opts.UseEntityFrameworkCoreTransactions();

    opts.Policies.AutoApplyTransactions();

    opts.UseFluentValidation();
    
    opts.OptimizeArtifactWorkflow();
});

var app = builder.Build();

// Configure the HTTP request pipeline.
app.UseSwagger();
app.UseSwaggerUI();

app.UseAuthorization();

app.MapGet("/orders/{orderId}", [Authorize] Results<BadRequest, Ok<Order>>(int orderId)
    => orderId > 999 ? TypedResults.BadRequest() : TypedResults.Ok(new Order(orderId)));

app.MapPost("/orders", Results<BadRequest, Ok<Order>>(CreateOrder command)
    => command.OrderId > 999 ? TypedResults.BadRequest() : TypedResults.Ok(new Order(command.OrderId)));

app.MapWolverineEndpoints(opts =>
{
    // This is strictly to test the endpoint policy
    opts.ConfigureEndpoints(c =>
    {
        // This adds metadata for OpenAPI
        c.WithMetadata(new CustomMetadata());
        c.WithTags("wolverine");
    });
    
    // Opting into the Fluent Validation middleware from
    // Wolverine.Http.FluentValidation
    opts.UseFluentValidationProblemDetailMiddleware();
    
    // Only want this middleware on endpoints on this one handler
    opts.AddMiddleware(typeof(BeforeAndAfterMiddleware),
        chain => chain.Method.HandlerType == typeof(MiddlewareEndpoints));

    opts.AddMiddlewareByMessageType(typeof(FakeAuthenticationMiddleware));
});

await app.RunOaktonCommands(args);