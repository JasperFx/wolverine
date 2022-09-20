using InMemoryMediator;
using Wolverine;
using Wolverine.EntityFrameworkCore;
using Wolverine.SqlServer;
using Microsoft.EntityFrameworkCore;

#region sample_InMediatorProgram

var builder = WebApplication.CreateBuilder(args);

var connectionString = builder.Configuration.GetConnectionString("SqlServer");

builder.Host.UseWolverine(opts =>
{
    // TODO -- use single helper that can read the connection string
    // from the DbContext
    opts.PersistMessagesWithSqlServer(connectionString);
    opts.UseEntityFrameworkCorePersistence();
});

// Register the EF Core DbContext
builder.Services.AddDbContext<ItemsDbContext>(
    x => x.UseSqlServer(connectionString),

    // This is important! Using Singleton scoping
    // of the options allows Wolverine + Lamar to significantly
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

app.MapPost("/items/create", (CreateItemCommand cmd, ICommandBus bus) => bus.InvokeAsync(cmd));

#endregion

#region sample_InMemoryMediator_WithResponseController

app.MapPost("/items/create2", (CreateItemCommand cmd, ICommandBus bus) => bus.InvokeAsync<ItemCreated>(cmd));

#endregion


app.MapControllers();

app.Run();
