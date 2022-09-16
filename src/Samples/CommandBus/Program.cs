using Baseline.Dates;
using CommandBusSamples;
using Wolverine;
using Wolverine.ErrorHandling;
using Wolverine.Persistence.Marten;
using Marten;
using Npgsql;
using Oakton;
using Oakton.Resources;

var builder = WebApplication.CreateBuilder(args);

builder.Host.ApplyOaktonExtensions();

builder.Services.AddSingleton<IRestaurantProxy, RealRestaurantProxy>();

// Normal Marten integration
builder.Services.AddMarten(opts =>
{
    opts.Connection("Host=localhost;Port=5433;Database=postgres;Username=postgres;password=postgres");
})
    // NEW! Adding Wolverine outbox integration to Marten in the "messages"
    // database schema
    .IntegrateWithWolverine("messages");

// Adding Wolverine as a straight up Command Bus
builder.Host.UseWolverine(opts =>
{
    // Just setting up some retries on transient database connectivity errors
    opts.Handlers.OnException<NpgsqlException>().OrInner<NpgsqlException>()
        .RetryWithCooldown(50.Milliseconds(), 100.Milliseconds(), 250.Milliseconds());

    // NEW! Apply the durable inbox/outbox functionality to the two in-memory queues
    opts.DefaultLocalQueue.UseDurableInbox();
    opts.LocalQueue("Notifications").UseDurableInbox();

    // And I just opened a GitHub issue to make this config easier...

    // BOO! Got to do this at least temporarily to help out a test runner
    opts.ApplicationAssembly = typeof(Program).Assembly;
});

builder.Services.AddResourceSetupOnStartup();
builder.Services.AddMvcCore(); // for JSON formatters

var app = builder.Build();

// This isn't *quite* the most efficient way to do this,
// but it's simple to understand, so please just let it go...
app.MapPost("/reservations", (AddReservation command, ICommandBus bus) => bus.EnqueueAsync(command));
app.MapPost("/reservations/confirm", (ConfirmReservation command, ICommandBus bus) => bus.EnqueueAsync(command));

// This opts into using Oakton for extended command line options for this app
// Oakton is also a transitive dependency of Wolverine itself
return await app.RunOaktonCommands(args);
