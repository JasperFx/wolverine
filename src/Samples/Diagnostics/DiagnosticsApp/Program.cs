using DiagnosticsModule;
using IntegrationTests;
using JasperFx.Core;
using JasperFx.Core.Reflection;
using Marten;
using Oakton;
using Oakton.Resources;
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

    opts.Discovery.IncludeTypesAsMessages(type => type.CanBeCastTo<IDiagnosticsMessage>());

    #endregion

    opts.UseRabbitMq().UseConventionalRouting();

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

await app.RunOaktonCommands(args);