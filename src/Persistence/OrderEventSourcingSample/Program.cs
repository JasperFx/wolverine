using JasperFx.Core;
using Marten;
using Marten.Exceptions;
using JasperFx;
using OrderEventSourcingSample;
using Wolverine;
using Wolverine.ErrorHandling;
using Wolverine.Marten;
using ConcurrencyException = Marten.Exceptions.ConcurrencyException;

var builder = WebApplication.CreateBuilder(args);

// Not 100% necessary, but enables some extra command line diagnostics
builder.Host.ApplyJasperFxExtensions();

// Adding Marten
builder.Services.AddMarten(opts =>
    {
        var connectionString = builder.Configuration.GetConnectionString("Marten");
        opts.Connection(connectionString);
        opts.DatabaseSchemaName = "orders";
    })

    // Adding the Wolverine integration for Marten.
    .IntegrateWithWolverine();

#region sample_configure_global_exception_rules

builder.Host.UseWolverine(opts =>
{
    // Retry policies if a Marten concurrency exception is encountered
    opts.OnException<ConcurrencyException>()
        .RetryOnce()
        .Then.RetryWithCooldown(100.Milliseconds(), 250.Milliseconds())
        .Then.Discard();
});

#endregion

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

var app = builder.Build();

app.UseSwagger();
app.UseSwaggerUI();

app.MapPost("/items/ready", (MarkItemReady command, IMessageBus bus) => bus.InvokeAsync(command));
app.MapGet("/", () => Results.Redirect("/swagger"));

return await app.RunJasperFxCommands(args);