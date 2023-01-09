using ItemService;
using Microsoft.EntityFrameworkCore;
using Oakton;
using Oakton.Resources;
using Wolverine;
using Wolverine.EntityFrameworkCore;
using Wolverine.SqlServer;

var builder = WebApplication.CreateBuilder(args);

// Just the normal work to get the connection string out of
// application configuration
var connectionString = builder.Configuration.GetConnectionString("sqlserver");

#region sample_optimized_efcore_registration

// If you're okay with this, this will register the DbContext as normally,
// but make some Wolverine specific optimizations at the same time
builder.Services.AddDbContextWithWolverineIntegration<ItemsDbContext>(
    x => x.UseSqlServer(connectionString));

#endregion

#region sample_registering_efcore_middleware

builder.Host.UseWolverine(opts =>
{
    // Setting up Sql Server-backed message storage
    // This requires a reference to Wolverine.SqlServer
    opts.PersistMessagesWithSqlServer(connectionString);

    // Set up Entity Framework Core as the support
    // for Wolverine's transactional middleware
    opts.UseEntityFrameworkCoreTransactions();
});

#endregion

// This is rebuilding the persistent storage database schema on startup
// and also clearing any persisted envelope state
builder.Host.UseResourceSetupOnStartup(StartupAction.ResetState);

builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

var app = builder.Build();

// Make sure the EF Core db is set up
await app.Services.GetRequiredService<ItemsDbContext>().Database.EnsureCreatedAsync();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.MapControllers();

app.MapPost("/items/create", (CreateItemCommand command, IMessageBus bus) => bus.InvokeAsync(command));

// Opt into using Oakton for command parsing
await app.RunOaktonCommands(args);