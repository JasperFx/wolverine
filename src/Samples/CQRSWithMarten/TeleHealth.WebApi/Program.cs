using JasperFx.Core;
using Marten;
using Marten.Events.Daemon.Resiliency;
using Marten.Events.Projections;
using Marten.Exceptions;
using Npgsql;
using JasperFx;
using TeleHealth.Common;
using Wolverine;
using Wolverine.ErrorHandling;
using Wolverine.Http;
using Wolverine.Marten;
using ConcurrencyException = Marten.Exceptions.ConcurrencyException;

var builder = WebApplication.CreateBuilder(args);

builder.Host.ApplyJasperFxExtensions();

#region sample_configuring_wolverine_event_subscriptions

builder.Host.UseWolverine(opts =>
{
    // I'm choosing to process any ChartingFinished event messages
    // in a separate, local queue with persistent messages for the inbox/outbox
    opts.PublishMessage<ChartingFinished>()
        .ToLocalQueue("charting")
        .UseDurableInbox();

    // If we encounter a concurrency exception, just try it immediately
    // up to 3 times total
    opts.Policies.OnException<ConcurrencyException>().RetryTimes(3);

    // It's an imperfect world, and sometimes transient connectivity errors
    // to the database happen
    opts.Policies.OnException<NpgsqlException>()
        .RetryWithCooldown(50.Milliseconds(), 100.Milliseconds(), 250.Milliseconds());

    // Automatic usage of transactional middleware as
    // Wolverine recognizes that an HTTP endpoint or message handler
    // persists data
    opts.Policies.AutoApplyTransactions();
});

#endregion

builder.Services.AddControllers();

// Learn more about configuring Swagger/OpenAPI at https://aka.ms/aspnetcore/swashbuckle
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

builder.Services.AddWolverineHttp();

// builder is a WebApplicationBuilder
// in this case
builder.Services.AddMarten(opts =>
{
    // More stuff...
    
});

#region sample_opting_into_wolverine_event_publishing

builder.Services.AddMarten(opts =>
    {
        var connString = builder
            .Configuration
            .GetConnectionString("marten");

        opts.Connection(connString);

        // There will be more here later...

        opts.Projections
            .Add<AppointmentDurationProjection>(ProjectionLifecycle.Async);

        // OR ???

        // opts.Projections
        //     .Add<AppointmentDurationProjection>(ProjectionLifecycle.Inline);

        opts.Projections.Add<AppointmentProjection>(ProjectionLifecycle.Inline);
        opts.Projections
            .Snapshot<ProviderShift>(SnapshotLifecycle.Async);
    })

    // This adds a hosted service to run
    // asynchronous projections in a background process
    .AddAsyncDaemon(DaemonMode.HotCold)

    // I added this to enroll Marten in the Wolverine outbox
    .IntegrateWithWolverine()

    // I also added this to opt into events being forward to
    // the Wolverine outbox during SaveChangesAsync()
    .EventForwardingToWolverine();

#endregion

var app = builder.Build();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.MapWolverineEndpoints();

// This is using the JasperFx library for command running
await app.RunJasperFxCommands(args);