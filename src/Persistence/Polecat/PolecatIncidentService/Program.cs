using Polecat;
using Polecat.Projections;
using PolecatIncidentService;
using Wolverine;
using Wolverine.Http;
using Wolverine.Polecat;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddOpenApi();

builder.Services.AddPolecat(opts =>
    {
        var connectionString = builder.Configuration.GetConnectionString("SqlServer")
                               ??
                               "Server=localhost,1434;User Id=sa;Password=P@55w0rd;Timeout=5;MultipleActiveResultSets=True;Initial Catalog=master;Encrypt=False";

        opts.ConnectionString = connectionString;
        opts.DatabaseSchemaName = "incidents";

        // We'll talk about this soon...
        opts.Projections.Snapshot<Incident>(SnapshotLifecycle.Inline);
    })

// For Marten users, *this* is the default for Polecat!
//.UseLightweightSessions()
    .IntegrateWithWolverine(x => x.UseWolverineManagedEventSubscriptionDistribution = true);

builder.Host.UseWolverine(opts => { opts.Policies.AutoApplyTransactions(); });

builder.Services.AddWolverineHttp();

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

// Adding Wolverine.HTTP
app.MapWolverineEndpoints();

// This gets you a lot of CLI goodness from the 
// greater JasperFx / Critter Stack ecosystem
// and will soon feed quite a bit of AI assisted development as well
return await app.RunJasperFxCommands(args);

// For test bootstrapping in case you want to work w/
// more than one system at a time
public partial class Program
{
}