using InMemoryMediator;
using JasperFx;
using Microsoft.EntityFrameworkCore;
using Wolverine;
using Wolverine.EntityFrameworkCore;
using Wolverine.SqlServer;

#region sample_InMediatorProgram

var builder = WebApplication.CreateBuilder(args);

// Using Weasel to make sure the items table exists
builder.Services.AddHostedService<DatabaseSchemaCreator>();

var connectionString = builder.Configuration.GetConnectionString("SqlServer");

builder.Host.UseWolverine(opts =>
{
    opts.PersistMessagesWithSqlServer(connectionString);

    // If you're also using EF Core, you may want this as well
    opts.UseEntityFrameworkCoreTransactions();
    
    opts.Policies.UseDurableLocalQueues();
    opts.Durability.KeepAfterMessageHandling = TimeSpan.FromHours(1);
    opts.LocalQueue("q1").UseDurableInbox();
});

// Register the EF Core DbContext
builder.Services.AddDbContext<ItemsDbContext>(
    x => x.UseSqlServer(connectionString),

    // This is weirdly important! Using Singleton scoping
    // of the options allows Wolverine to significantly
    // optimize the runtime pipeline of the handlers that
    // use this DbContext type
    optionsLifetime: ServiceLifetime.Singleton);

#endregion

builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

var app = builder.Build();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

#region sample_InMemoryMediator_UseWolverineAsMediatorController

app.MapPost("/items/create", (CreateItemCommand cmd, IMessageBus bus) => bus.InvokeAsync(cmd));

#endregion

#region sample_InMemoryMediator_WithResponseController

app.MapPost("/items/create2", (CreateItemCommand cmd, IMessageBus bus) => bus.InvokeAsync<ItemCreated>(cmd));

#endregion


app.MapControllers();

return await app.RunJasperFxCommands(args);