using JasperFx;
using JasperFx.Events.Daemon;
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
        ?? "Server=localhost,1434;User Id=sa;Password=P@55w0rd;Timeout=5;MultipleActiveResultSets=True;Initial Catalog=master;Encrypt=False";

    opts.ConnectionString = connectionString;
    opts.DatabaseSchemaName = "incidents";

    opts.Projections.Snapshot<Incident>(SnapshotLifecycle.Inline);
})
.UseLightweightSessions()
.AddAsyncDaemon(DaemonMode.HotCold)
.IntegrateWithWolverine();

builder.Host.UseWolverine(opts =>
{
    opts.Policies.AutoApplyTransactions();
});

builder.Services.AddWolverineHttp();

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.MapWolverineEndpoints();

return await app.RunJasperFxCommands(args);

// For test bootstrapping
public partial class Program{}
