using Marten;
using JasperFx;
using JasperFx.Resources;
using Wolverine;
using Wolverine.Http;
using Wolverine.Marten;

var builder = WebApplication.CreateBuilder(args);

var connectionString = "Host=localhost;Port=5433;Database=postgres;Username=postgres;password=postgres";

#region sample_configuring_wolverine_for_marten_multi_tenancy

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
    .IntegrateWithWolverine(x => x.MasterDatabaseConnectionString = connectionString);

#endregion

#region sample_add_resource_setup_on_startup

builder.Services.AddResourceSetupOnStartup();

#endregion

#region sample_wolverine_setup_for_marten_multitenancy

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

#endregion

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

builder.Services.AddWolverineHttp();

#region sample_configuring_tenant_id_detection_for_todo_service

// Let's add in Wolverine HTTP endpoints to the routing tree
app.MapWolverineEndpoints(opts =>
{
    // Letting Wolverine HTTP automatically detect the tenant id!
    opts.TenantId.IsRouteArgumentNamed("tenant");

    // Assert that the tenant id was successfully detected,
    // or pull the rip cord on the request and return a
    // 400 w/ ProblemDetails
    opts.TenantId.AssertExists();
});

#endregion

return await app.RunJasperFxCommands(args);