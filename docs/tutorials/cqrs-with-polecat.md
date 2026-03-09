# Event Sourcing and CQRS with Polecat

::: tip
This guide assumes some familiarity with Event Sourcing nomenclature.
:::

Let's get the entire Wolverine + [Polecat](https://github.com/JasperFx/polecat) combination assembled and build a system using CQRS with Event Sourcing!

We're going to start with a simple, headless ASP.Net Core project:

```bash
dotnet new webapi
```

Next, add the `WolverineFx.Http.Polecat` Nuget to get Polecat, Wolverine itself, and the full Wolverine + Polecat integration
including the HTTP integration. Inside the bootstrapping in the `Program` file:

```csharp
builder.Services.AddPolecat(opts =>
{
    var connectionString = builder.Configuration.GetConnectionString("Polecat");
    opts.Connection(connectionString);
})
.IntegrateWithWolverine();
```

For Wolverine itself:

```csharp
builder.Host.UseWolverine(opts =>
{
    opts.Policies.AutoApplyTransactions();
});

builder.Services.AddWolverineHttp();
```

Next, add support for Wolverine.HTTP endpoints:

```csharp
app.MapWolverineEndpoints();
```

And lastly, add the extended command line support through Oakton:

```csharp
return await app.RunOaktonCommands(args);
```

## Event Types and a Projected Aggregate

In Polecat, a "Projection" is the mechanism of taking raw events and "projecting" them
into some kind of view, which could be a .NET object persisted to the database as JSON.

```cs
public class Incident
{
    public Guid Id { get; set; }
    public int Version { get; set; }
    public IncidentStatus Status { get; set; } = IncidentStatus.Pending;
    public IncidentCategory? Category { get; set; }
    public bool HasOutstandingResponseToCustomer { get; set; } = false;

    public Incident() { }

    public void Apply(IncidentLogged _) { }
    public void Apply(AgentRespondedToIncident _) => HasOutstandingResponseToCustomer = false;
    public void Apply(CustomerRespondedToIncident _) => HasOutstandingResponseToCustomer = true;
    public void Apply(IncidentResolved _) => Status = IncidentStatus.Resolved;
    public void Apply(IncidentClosed _) => Status = IncidentStatus.Closed;
}
```

And some event types:

```cs
public record IncidentLogged(Guid CustomerId, Contact Contact, string Description, Guid LoggedBy);
public record IncidentCategorised(Guid IncidentId, IncidentCategory Category, Guid CategorisedBy);
public record IncidentClosed(Guid ClosedBy);
```

## Start a New Stream

Here's the HTTP endpoint that will log a new incident by starting a new event stream:

```cs
public record LogIncident(Guid CustomerId, Contact Contact, string Description, Guid LoggedBy);

public static class LogIncidentEndpoint
{
    [WolverinePost("/api/incidents")]
    public static (CreationResponse<Guid>, IStartStream) Post(LogIncident command)
    {
        var (customerId, contact, description, loggedBy) = command;

        var logged = new IncidentLogged(customerId, contact, description, loggedBy);
        var start = PolecatOps.StartStream<Incident>(logged);

        var response = new CreationResponse<Guid>("/api/incidents/" + start.StreamId, start.StreamId);

        return (response, start);
    }
}
```

The `IStartStream` interface is a [Polecat specific "side effect"](/guide/durability/polecat/operations) type. `PolecatOps.StartStream()` assigns a new sequential `Guid` value for the new incident.

One of the biggest advantages of Wolverine is that it allows you to use pure functions for many handlers, and the unit test becomes just:

```cs
[Fact]
public void unit_test()
{
    var contact = new Contact(ContactChannel.Email);
    var command = new LogIncident(Guid.NewGuid(), contact, "It's broken", Guid.NewGuid());

    var (response, startStream) = LogIncidentEndpoint.Post(command);

    startStream.Events.ShouldBe([
        new IncidentLogged(command.CustomerId, command.Contact, command.Description, command.LoggedBy)
    ]);
}
```

## Appending Events to an Existing Stream

Let's write an HTTP endpoint to accept a `CategoriseIncident` command using the [aggregate handler workflow](/guide/durability/polecat/event-sourcing):

```cs
public record CategoriseIncident(IncidentCategory Category, Guid CategorisedBy, int Version);

public static class CategoriseIncidentEndpoint
{
    public static ProblemDetails Validate(Incident incident)
    {
        return incident.Status == IncidentStatus.Closed
            ? new ProblemDetails { Detail = "Incident is already closed" }
            : WolverineContinue.NoProblems;
    }

    [EmptyResponse]
    [WolverinePost("/api/incidents/{incidentId:guid}/category")]
    public static IncidentCategorised Post(
        CategoriseIncident command,
        [Aggregate("incidentId")] Incident incident)
    {
        return new IncidentCategorised(incident.Id, command.Category, command.CategorisedBy);
    }
}
```

Behind the scenes, Wolverine is using Polecat's `FetchForWriting` API which sets up optimistic concurrency checks.

## Publishing or Handling Events

The Wolverine + Polecat combination comes with two main ways to process events:

[Event Forwarding](/guide/durability/polecat/event-forwarding) is a lightweight way to immediately publish events through Wolverine's messaging infrastructure. Note that event forwarding comes with **no ordering guarantees**.

[Event Subscriptions](/guide/durability/polecat/subscriptions) utilizes a **strictly ordered mechanism** to read in and process event data from the Polecat event store. Wolverine supports three modes:

1. Executing each event with a Wolverine message handler in strict order
2. Publishing the events as messages through Wolverine in strict order
3. User defined operations on a batch of events at a time

## Scaling Polecat Projections

Wolverine has the ability to distribute the asynchronous projections and subscriptions to Polecat events evenly across
an application cluster for better scalability. See [Projection/Subscription Distribution](/guide/durability/polecat/distribution) for more information.

## Observability

Both Polecat and Wolverine have strong support for OpenTelemetry tracing as well as emitting performance
metrics. See [Wolverine's Otel and Metrics](/guide/logging#open-telemetry) support for more information.
