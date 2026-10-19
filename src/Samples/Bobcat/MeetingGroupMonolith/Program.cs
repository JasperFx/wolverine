using JasperFx.Events.Projections;
using Marten;
using Marten.Events.Projections;
using MeetingGroupMonolith;
using Payments;
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
        ?? "Host=localhost;Port=5433;Database=meeting_groups;Username=postgres;Password=postgres";

    opts.Connection(connectionString);

    // Schema-per-module — each module's documents are isolated
    opts.Schema.For<UserAccess.User>().DatabaseSchemaName("users");
    opts.Schema.For<Administration.MeetingGroupProposal>().DatabaseSchemaName("administration");
    opts.Schema.For<Meetings.MeetingGroup>().DatabaseSchemaName("meetings");
    opts.Schema.For<Meetings.Meeting>().DatabaseSchemaName("meetings");
    opts.Schema.For<Meetings.Member>().DatabaseSchemaName("meetings");

    // Event sourcing for the Payments module — replaces SqlStreamStore
    opts.Events.DatabaseSchemaName = "payments";
    opts.Projections.Snapshot<Subscription>(SnapshotLifecycle.Inline);
})
.IntegrateWithWolverine()
.UseLightweightSessions();

builder.Host.UseWolverine(opts =>
{
    opts.Discovery.IncludeAssembly(typeof(Program).Assembly);
    opts.Policies.AutoApplyTransactions();
    opts.UseFluentValidation();
    opts.ServiceName = "MeetingGroups";

    // Durable local queues for inter-module messaging
    // Messages survive restarts via the Marten outbox
    opts.LocalQueue("registrations").UseDurableInbox();
    opts.LocalQueue("administration").UseDurableInbox();
    opts.LocalQueue("meetings").UseDurableInbox();
    opts.LocalQueue("payments").UseDurableInbox();

    // Route integration events to module-specific durable queues
    opts.Publish(x =>
    {
        x.Message<NewUserRegisteredEvent>();
        x.ToLocalQueue("meetings");
    });

    opts.Publish(x =>
    {
        x.Message<MeetingGroupProposalAcceptedEvent>();
        x.ToLocalQueue("meetings");
    });

    opts.Publish(x =>
    {
        x.Message<MeetingAttendeeAddedEvent>();
        x.ToLocalQueue("payments");
    });

    opts.Publish(x =>
    {
        x.Message<SubscriptionExpirationChangedEvent>();
        x.ToLocalQueue("meetings");
    });
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
