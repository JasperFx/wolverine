using DiagnosticsModule;
using IntegrationTests;
using JasperFx.Core;
using Marten;
using JasperFx;
using JasperFx.Resources;
using Wolverine;
using Wolverine.ErrorHandling;
using Wolverine.Http;
using Wolverine.Marten;
using Wolverine.RabbitMQ;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.

// Learn more about configuring Swagger/OpenAPI at https://aka.ms/aspnetcore/swashbuckle
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

builder.Services.AddWolverineHttp();

builder.Services.AddAuthorization();

builder.Services.AddMarten(opts =>
{
    opts.Connection(Servers.PostgresConnectionString);
    opts.DatabaseSchemaName = "http";
}).IntegrateWithWolverine();


builder.Services.AddResourceSetupOnStartup();

// Need this.
builder.Host.UseWolverine(opts =>
{
    opts.Policies.AutoApplyTransactions();

    #region sample_use_your_own_marker_type

    opts.Discovery.CustomizeHandlerDiscovery(types => types.Includes.Implements<IDiagnosticsMessageHandler>());

    #endregion

    opts.ServiceName = "DescriptiveName";

    opts.UseRabbitMq().AutoProvision()
        .UseConventionalRouting(c =>
        {
            c.QueueNameForListener(t => "service1." + t.FullName);
        });

    opts.Policies.OnException<BadImageFormatException>().Discard();
    opts.Policies.OnException<InvalidOperationException>()
        .ScheduleRetry(1.Minutes());
    opts.Policies.OnAnyException().RetryOnce().Then.RetryWithCooldown(100.Milliseconds(), 250.Milliseconds());
});

var app = builder.Build();

// Configure the HTTP request pipeline.
app.UseSwagger();
app.UseSwaggerUI();

app.UseAuthorization();

app.MapWolverineEndpoints(opts => { });

// This is an extension method within JasperFx
// And it's important to relay the exit code
// from JasperFx commands to the command line
// if you want to use these tools in CI or CD
// pipelines to denote success or failure
return await app.RunJasperFxCommands(args);