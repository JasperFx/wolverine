using BookingMonolith;
using JasperFx.Events.Projections;
using Marten;
using Marten.Events.Projections;
using Wolverine;
using Wolverine.FluentValidation;
using Wolverine.Http;
using Wolverine.Http.FluentValidation;
using Wolverine.Marten;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

builder.Services.AddWolverineHttp();

builder.Services.AddMarten(opts =>
{
    var connectionString = builder.Configuration.GetConnectionString("Marten")
        ?? "Host=localhost;Port=5433;Database=booking;Username=postgres;Password=postgres";

    opts.Connection(connectionString);

    // Schema-per-module
    opts.Schema.For<Identity.UserAccount>().DatabaseSchemaName("identity");
    opts.Schema.For<Passenger.Passenger>().DatabaseSchemaName("passenger");
    opts.Schema.For<Flight.Flight>().DatabaseSchemaName("flight");
    opts.Schema.For<Flight.Aircraft>().DatabaseSchemaName("flight");
    opts.Schema.For<Flight.Airport>().DatabaseSchemaName("flight");
    opts.Schema.For<Flight.Seat>().DatabaseSchemaName("flight");

    // Event sourcing for the Booking module — replaces EventStoreDB + MongoDB
    opts.Events.DatabaseSchemaName = "booking";
    opts.Projections.Snapshot<Booking.BookingRecord>(SnapshotLifecycle.Inline);
})
.IntegrateWithWolverine()
.UseLightweightSessions();

builder.Host.UseWolverine(opts =>
{
    opts.Discovery.IncludeAssembly(typeof(Program).Assembly);
    opts.Policies.AutoApplyTransactions();
    opts.UseFluentValidation();
    opts.ServiceName = "BookingMonolith";

    // Durable local queues for inter-module messaging
    opts.LocalQueue("identity").UseDurableInbox();
    opts.LocalQueue("passenger").UseDurableInbox();
    opts.LocalQueue("flight").UseDurableInbox();
    opts.LocalQueue("booking").UseDurableInbox();

    // Route integration events
    opts.Publish(x => { x.Message<UserCreated>(); x.ToLocalQueue("passenger"); });
    opts.Publish(x => { x.Message<PassengerCreated>(); x.ToLocalQueue("booking"); });
    opts.Publish(x => { x.Message<FlightCreated>(); x.ToLocalQueue("booking"); });
    opts.Publish(x => { x.Message<BookingCreated>(); x.ToLocalQueue("flight"); });
});

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.MapWolverineEndpoints(opts =>
{
    opts.UseFluentValidationProblemDetailMiddleware();
});

await app.RunAsync();
