using Marten;
using MultiTenantedTodoWebService;
using Oakton;
using Oakton.Resources;
using Wolverine;
using Wolverine.Http;
using Wolverine.Marten;

var builder = WebApplication.CreateBuilder(args);

var connectionString = "Host=localhost;Port=5433;Database=postgres;Username=postgres;password=postgres";

// Adding Marten for persistence
builder.Services.AddMarten(m =>
    {
        // With multi-tenancy through a database per tenant
        m.MultiTenantedDatabases(tenancy =>
        {
            // You would probably be pulling the connection strings out of configuration,
            // but it's late in the afternoon and I'm being lazy building out this sample!
            tenancy.AddSingleTenantDatabase("Host=localhost;Port=5433;Database=tenant1;Username=postgres;password=postgres", "tenant1");
            tenancy.AddSingleTenantDatabase("Host=localhost;Port=5433;Database=tenant2;Username=postgres;password=postgres", "tenant2");
            tenancy.AddSingleTenantDatabase("Host=localhost;Port=5433;Database=tenant3;Username=postgres;password=postgres", "tenant3");

        });

        
        m.DatabaseSchemaName = "mttodo";
    })
    .IntegrateWithWolverine(masterDatabaseConnectionString:connectionString);



builder.Services.AddResourceSetupOnStartup();

// Wolverine usage is required for WolverineFx.Http
builder.Host.UseWolverine(opts =>
{
    // This middleware will apply to the HTTP
    // endpoints as well
    opts.Policies.AutoApplyTransactions();
    
    // Setting up the outbox on all locally handled
    // background tasks
    opts.Policies.UseDurableLocalQueues();
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

// Let's add in Wolverine HTTP endpoints to the routing tree
app.MapWolverineEndpoints();

return await app.RunOaktonCommands(args);