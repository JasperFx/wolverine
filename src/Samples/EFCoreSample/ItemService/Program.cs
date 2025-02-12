using ItemService;
using Microsoft.EntityFrameworkCore;
using JasperFx;
using JasperFx.Resources;
using Wolverine;
using Wolverine.EntityFrameworkCore;
using Wolverine.Http;
using Wolverine.SqlServer;

var builder = WebApplication.CreateBuilder(args);

// Using Weasel to make sure the items table exists
builder.Services.AddHostedService<DatabaseSchemaCreator>();

// Just the normal work to get the connection string out of
// application configuration
var connectionString = builder.Configuration.GetConnectionString("sqlserver");

#region sample_optimized_efcore_registration

// If you're okay with this, this will register the DbContext as normally,
// but make some Wolverine specific optimizations at the same time
builder.Services.AddDbContextWithWolverineIntegration<ItemsDbContext>(
    x => x.UseSqlServer(connectionString), "wolverine");

#endregion

#region registration_of_db_context_not_integrated_with_outbox

// Add DbContext that is not integrated with outbox
builder.Services.AddDbContext<ItemsDbContextWithoutOutbox>(
    x => x.UseSqlServer(connectionString));

#endregion

#region sample_registering_efcore_middleware

builder.Host.UseWolverine(opts =>
{
    // Setting up Sql Server-backed message storage
    // This requires a reference to Wolverine.SqlServer
    opts.PersistMessagesWithSqlServer(connectionString, "wolverine");

    // Set up Entity Framework Core as the support
    // for Wolverine's transactional middleware
    opts.UseEntityFrameworkCoreTransactions();

    // Enrolling all local queues into the
    // durable inbox/outbox processing
    opts.Policies.UseDurableLocalQueues();
});

#endregion

#region sample_resource_setup_on_startup

// This is rebuilding the persistent storage database schema on startup
builder.Host.UseResourceSetupOnStartup();

#endregion

builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

builder.Services.AddWolverineHttp();

var app = builder.Build();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.MapControllers();

app.MapWolverineEndpoints();

app.MapPost("/items/create", (CreateItemCommand command, IMessageBus bus) => bus.InvokeAsync(command));

app.MapPost("/items/createWithDbContextNotIntegratedWithOutbox", (CreateItemWithDbContextNotIntegratedWithOutboxCommand command, IMessageBus bus) => bus.InvokeAsync(command));

#region sample_using_jasperfx_for_command_line_parsing

// Opt into using JasperFx for command parsing
await app.RunJasperFxCommands(args);

#endregion