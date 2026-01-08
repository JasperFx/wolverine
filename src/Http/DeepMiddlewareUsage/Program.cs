using DeepMiddlewareUsage;
using IntegrationTests;
using JasperFx;
using Marten;
using Wolverine;
using Wolverine.Http;
using Wolverine.Marten;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddMarten(options =>
    {
        options.Connection(Servers.PostgresConnectionString);
        options.DatabaseSchemaName = "trainers";
    })
    .IntegrateWithWolverine();

builder.Host.ApplyJasperFxExtensions();

builder.Host.UseWolverine(options =>
{
    // Setting up the outbox on all locally handled
    // background tasks
    options.Policies.AutoApplyTransactions();
    options.Policies.UseDurableLocalQueues();
    options.Policies.UseDurableOutboxOnAllSendingEndpoints();
});

builder.Services.AddWolverineHttp();

WebApplication app = builder.Build();

app.MapWolverineEndpoints(options =>
{
    options.AddMiddleware(typeof(UserIdMiddleWare));
    options.AddPolicy<RequestTrainerPolicy>();
    options.ConfigureEndpoints(e => { });
});

return await app.RunJasperFxCommands(args);