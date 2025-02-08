using CommandBusSamples;
using JasperFx.Core;
using Marten;
using Marten.AspNetCore;
using Npgsql;
using JasperFx;
using JasperFx.Resources;
using Wolverine;
using Wolverine.ErrorHandling;
using Wolverine.Marten;

var builder = WebApplication.CreateBuilder(args);

builder.Host.ApplyJasperFxExtensions();

// Learn more about configuring Swagger/OpenAPI at https://aka.ms/aspnetcore/swashbuckle
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

builder.Services.AddSingleton<IRestaurantProxy, RealRestaurantProxy>();

// Normal Marten integration
builder.Services.AddMarten(opts =>
    {
        opts.Connection("Host=localhost;Port=5433;Database=postgres;Username=postgres;password=postgres");
    })
    // Adding Wolverine outbox integration to Marten in the "messages"
    // database schema
    .IntegrateWithWolverine(x => x.MessageStorageSchemaName = "messages");

// Adding Wolverine as a straight up Command Bus
builder.Host.UseWolverine(opts =>
{
    // Just setting up some retries on transient database connectivity errors
    opts.Policies.OnException<NpgsqlException>().OrInner<NpgsqlException>()
        .RetryWithCooldown(50.Milliseconds(), 100.Milliseconds(), 250.Milliseconds());

    // Apply the durable inbox/outbox functionality to the two in-memory queues
    opts.DefaultLocalQueue.UseDurableInbox();
    opts.LocalQueue("Notifications").UseDurableInbox();
});

builder.Services.AddResourceSetupOnStartup();
builder.Services.AddMvcCore(); // for JSON formatters

var app = builder.Build();

// This isn't *quite* the most efficient way to do this,
// but it's simple to understand, so please just let it go...
app.MapPost("/reservations", (AddReservation command, IMessageBus bus) => bus.PublishAsync(command));
app.MapPost("/reservations/confirm", (ConfirmReservation command, IMessageBus bus) => bus.PublishAsync(command));

// Query for all open reserviationsoutloo
app.MapGet("/reservations",
    (HttpContext context, IQuerySession session) => session.Query<Reservation>().WriteArray(context));

app.UseSwagger();
app.UseSwaggerUI();

// This opts into using JasperFx for extended command line options for this app
// JasperFx is also a transitive dependency of Wolverine itself
return await app.RunJasperFxCommands(args);