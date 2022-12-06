using AppWithMiddleware;
using IntegrationTests;
using Marten;
using Oakton;
using Wolverine;
using Wolverine.FluentValidation;
using Wolverine.Marten;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddMarten(opts =>
{
    opts.Connection(Servers.PostgresConnectionString);
    opts.DatabaseSchemaName = "wolverine_middleware";
}).IntegrateWithWolverine().ApplyAllDatabaseChangesOnStartup();

builder.Host.UseWolverine(opts =>
{
    opts.Handlers.AddMiddlewareByMessageType(typeof(AccountLookupMiddleware));
    
    // This is a bug, Jeremy should fix ASAP
    opts.ApplicationAssembly = typeof(Program).Assembly;
    
    // This will register all the Fluent Validation validators, and
    // apply validation middleware where the command type has
    // a validator
    opts.UseFluentValidation();
});

// Learn more about configuring Swagger/OpenAPI at https://aka.ms/aspnetcore/swashbuckle
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

var app = builder.Build();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseHttpsRedirection();

return await app.RunOaktonCommands(args);