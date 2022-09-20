using Baseline.Dates;
using Wolverine;
using Wolverine.ErrorHandling;
using Wolverine.Marten;
using Marten;
using Marten.Exceptions;
using Oakton;
using OrderEventSourcingSample;

var builder = WebApplication.CreateBuilder(args);

// Not 100% necessary, but enables some extra command line diagnostics
builder.Host.ApplyOaktonExtensions();

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
    opts.Handlers.OnException<ConcurrencyException>()
        .RetryOnce()
        .Then.RetryWithCooldown(100.Milliseconds(), 250.Milliseconds())
        .Then.Discard();
});

#endregion

var app = builder.Build();

app.MapPost("/items/ready", (MarkItemReady command, ICommandBus bus) => bus.InvokeAsync(command));

return await app.RunOaktonCommands(args);
