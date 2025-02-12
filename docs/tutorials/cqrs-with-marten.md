# Event Sourcing and CQRS with Marten

::: info
Sadly enough, what's now Wolverine was mostly an abandoned project during the COVID years. It was
rescued and rebooted specifically to form a full blown CQRS with Event Sourcing stack in combination
with Marten using what we now call the "aggregate handler workflow." At this point, the "Critter Stack" team
firmly believes that this is the very most robust and productive tooling for CQRS with Event Sourcing in the 
entire .NET ecosystem.
:::

::: tip
This guide assumes some familiarity with Event Sourcing nomenclature, but if you're relative new to that style
of development, see [Understanding Event Sourcing with Marten](https://martendb.io/events/learning.html) from the Marten documentation.
:::

Let's get the entire "Critter Stack" (Wolverine + [Marten](https://martendb.io)) assembled and build a system using CQRS with Event Sourcing!

We'll be using the [IncidentService](https://github.com/jasperfx/wolverine/tree/main/src/Samples/IncidentService) example service to show an example of using Wolverine with Marten in a headless
web service with its accompanying test harness. The problem domain is pretty familiar to all of us developers because our
lives are somewhat managed by issue tracking systems of some soft. Starting with some [Event Storming](https://jeremydmiller.com/2023/11/28/building-a-critter-stack-application-event-storming/), the first couple
events and triggering commands might be something like this:

![Event Storming](/event-storming.png)

We're going to start with a simple, headless ASP.Net Core project like so (and delete the silly weather forecast stuff):

```bash
dotnet add webapi
```

Next, add the `WolverineFx.Http.Marten` Nuget to get Marten, Wolverine itself, and the full Wolverine + Marten integration
including the HTTP integration. Inside the bootstrapping in the `Program` file, we'll start with this to bootstrap just Marten:

```csharp
builder.Services.AddMarten(opts =>
{
    var connectionString = builder.Configuration.GetConnectionString("Marten");
    opts.Connection(connectionString);
    opts.DatabaseSchemaName = "incidents";
})

// This adds configuration with Wolverine's transactional outbox and
// Marten middleware support to Wolverine
.IntegrateWithWolverine();
```

For Wolverine itself, we'll start simply:

```csharp
builder.Host.UseWolverine(opts =>
{
    // This is almost an automatic default to have
    // Wolverine apply transactional middleware to any
    // endpoint or handler that uses persistence services
    opts.Policies.AutoApplyTransactions();
});

// To add Wolverine.HTTP services to the IoC container
builder.Services.AddWolverineHttp();
```

::: info
We had to separate the IoC service registrations from the addition of the 
Wolverine endpoints when Wolverine was decoupled from Lamar as its only
IoC tool. Two steps forward, one step back.
:::

Next, let's add support for [Wolverine.HTTP]() endpoints:

```csharp
app.MapWolverineEndpoints();
```
And *lastly*, let's add the extended command line support through [Oakton](https://jasperfx.github.io/oakton) (don't worry, that's a 
transitive dependency of Wolverine and you're good to go):

```csharp
// Using the expanded command line options for the Critter Stack
// that are helpful for code generation, database migrations, and diagnostics
return await app.RunOaktonCommands(args);
```

## Event Types and a Projected Aggregate

::: tip
In Marten parlance, a "Projection" is the mechanism of taking raw Marten events and "projecting" them
into some kind of view, which could be a .NET object that may or may not be persisted to the database as
JSON (PostgreSQL JSONB to be precise) or [flat table projections](/events/projections/flat) that write to old fashioned relational database
tables.

The phrase "aggregate" is hopelessly overloaded in Event Sourcing and DDD communities. In Marten world we mostly
just use the word "aggregate" to mean a projected document that is built up by a stream or cross stream of events.
:::

In a real project, the event types and especially any projected documents will be designed as you go
and will probably evolve through subsequent user stories. We're starting from an existing sample project,
so we're going to skip ahead to some of our initial event types:

<!-- snippet: sample_Incident_aggregate -->
<a id='snippet-sample_incident_aggregate'></a>
```cs
public class Incident
{
    public Guid Id { get; set; }
    
    // THIS IS IMPORTANT! Marten will set this itself, and you
    // can use this to communicate the current version to clients
    // as a way to opt into optimistic concurrency checks to prevent
    // problems from concurrent access
    public int Version { get; set; }
    public IncidentStatus Status { get; set; } = IncidentStatus.Pending;
    public IncidentCategory? Category { get; set; }
    public bool HasOutstandingResponseToCustomer { get; set; } = false;

    // Make serialization easy
    public Incident()
    {
    }

    public void Apply(AgentRespondedToIncident _) => HasOutstandingResponseToCustomer = false;

    public void Apply(CustomerRespondedToIncident _) => HasOutstandingResponseToCustomer = true;

    public void Apply(IncidentResolved _) => Status = IncidentStatus.Resolved;

    public void Apply(ResolutionAcknowledgedByCustomer _) => Status = IncidentStatus.ResolutionAcknowledgedByCustomer;

    public void Apply(IncidentClosed _) => Status = IncidentStatus.Closed;

    public bool ShouldDelete(Archived @event) => true;
}
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Samples/IncidentService/IncidentService/Incident.cs#L74-L107' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_incident_aggregate' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

::: info
You can use immutable `record` types for the aggregate documents, and sometimes you might want to. I think
the code comes out a little simpler without the immutability, so I converted the `Incident` type to be mutable
as part of writing out this guide. Also, it's a touch less efficient to use immutability due to the extra object
allocations. No free lunch folks.
:::

And here's a smattering of some of the first events we'll capture:

<!-- snippet: sample_incident_events -->
<a id='snippet-sample_incident_events'></a>
```cs
public record IncidentLogged(
    Guid CustomerId,
    Contact Contact,
    string Description,
    Guid LoggedBy
);

public record IncidentCategorised(
    Guid IncidentId,
    IncidentCategory Category,
    Guid CategorisedBy
);

public record IncidentPrioritised(
    Guid IncidentId,
    IncidentPriority Priority,
    Guid PrioritisedBy,
    DateTimeOffset PrioritisedAt
);

public record IncidentClosed(
    Guid ClosedBy
);
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Samples/IncidentService/IncidentService/Incident.cs#L5-L31' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_incident_events' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

Many people -- myself included -- prefer to use `record` types for the event types. I would deviate from that though
if the code is easier to read by doing property assignments if there are a *lot* of values to copy from a command to
the event objects. In other words, I'm just not a fan of really big constructor function signatures.

## Start a New Stream

So of course we're going to use a [Vertical Slice Architecture](/tutorials/vertical-slice-architecture) approach for
our code, so here's the first cut at the HTTP endpoint that will log a new incident by starting a new event stream
for the incident in one file:

<!-- snippet: sample_LogIncident -->
<a id='snippet-sample_logincident'></a>
```cs
public record LogIncident(
    Guid CustomerId,
    Contact Contact,
    string Description,
    Guid LoggedBy
);

public static class LogIncidentEndpoint
{
    [WolverinePost("/api/incidents")]
    public static (CreationResponse<Guid>, IStartStream) Post(LogIncident command)
    {
        var (customerId, contact, description, loggedBy) = command;

        var logged = new IncidentLogged(customerId, contact, description, loggedBy);
        var start = MartenOps.StartStream<Incident>(logged);

        var response = new CreationResponse<Guid>("/api/incidents/" + start.StreamId, start.StreamId);
        
        return (response, start);
    }
}
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Samples/IncidentService/IncidentService/LogIncident.cs#L7-L32' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_logincident' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

And maybe there's a few details to unpack. It might help to [see the code](/guide/codegen) that Wolverine generates for this HTTP
endpoint: 

```csharp
    public class POST_api_incidents : Wolverine.Http.HttpHandler
    {
        private readonly Wolverine.Http.WolverineHttpOptions _wolverineHttpOptions;
        private readonly Wolverine.Runtime.IWolverineRuntime _wolverineRuntime;
        private readonly Wolverine.Marten.Publishing.OutboxedSessionFactory _outboxedSessionFactory;

        public POST_api_incidents(Wolverine.Http.WolverineHttpOptions wolverineHttpOptions, Wolverine.Runtime.IWolverineRuntime wolverineRuntime, Wolverine.Marten.Publishing.OutboxedSessionFactory outboxedSessionFactory) : base(wolverineHttpOptions)
        {
            _wolverineHttpOptions = wolverineHttpOptions;
            _wolverineRuntime = wolverineRuntime;
            _outboxedSessionFactory = outboxedSessionFactory;
        }



        public override async System.Threading.Tasks.Task Handle(Microsoft.AspNetCore.Http.HttpContext httpContext)
        {
            var messageContext = new Wolverine.Runtime.MessageContext(_wolverineRuntime);
            // Building the Marten session
            await using var documentSession = _outboxedSessionFactory.OpenSession(messageContext);
            // Reading the request body via JSON deserialization
            var (command, jsonContinue) = await ReadJsonAsync<IncidentService.LogIncident>(httpContext);
            if (jsonContinue == Wolverine.HandlerContinuation.Stop) return;
            
            // The actual HTTP request handler execution
            (var creationResponse_response, var startStream) = IncidentService.LogIncidentEndpoint.Post(command);

            if (startStream != null)
            {
                
                // Placed by Wolverine's ISideEffect policy
                startStream.Execute(documentSession);

            }

            // This response type customizes the HTTP response
            ApplyHttpAware(creationResponse_response, httpContext);
            
            // Save all pending changes to this Marten session
            await documentSession.SaveChangesAsync(httpContext.RequestAborted).ConfigureAwait(false);

            
            // Have to flush outgoing messages just in case Marten did nothing because of https://github.com/JasperFx/wolverine/issues/536
            await messageContext.FlushOutgoingMessagesAsync().ConfigureAwait(false);

            // Writing the response body to JSON because this was the first 'return variable' in the method signature
            await WriteJsonAsync(httpContext, creationResponse_response);
        }

    }
```


Just to rewind from the bootstrapping code up above, we had
this line of code in the Wolverine setup to turn on [transactional middleware](/guide/durability/marten/transactional-middleware) by default:

```csharp
    // This is almost an automatic default to have
    // Wolverine apply transactional middleware to any
    // endpoint or handler that uses persistence services
    opts.Policies.AutoApplyTransactions();
```

That directive tells Wolverine to use a Marten `IDocumentSession`, enroll it in the Wolverine transactional
outbox just in case, and finally to call `SaveChangesAsync()` after the main handler. The `IStartStream` interface
is a [Marten specific "side effect"](/guide/durability/marten/operations) type that tells Wolverine that this endpoint is applying changes to Marten.

`MartenOps.StartStream()` is assigning a new sequential `Guid` value for the new incident. The [`CreationResponse`](/guide/http/metadata.html#ihttpaware-or-iendpointmetadataprovider-models)
type is a special type in Wolverine used to:

1. Embed the new incident id as the `Value` property in the JSON sent back to the client
2. Write out a 201 http status code to denote a new resource was created
3. Communicate the Url of the new resource created, which in this case is the intended Url for a `GET` endpoint we'll write later
   to return the `Incident` state for a given event stream

One of the biggest advantages of Wolverine is that allows you to use [pure functions](https://jeremydmiller.com/2024/01/10/building-a-critter-stack-application-easy-unit-testing-with-pure-functions/) for many handlers or HTTP endpoints,
and this is no different. That endpoint above is admittedly using some Wolverine types to express the intended functionality
through return values, but the unit test becomes just this:

<!-- snippet: sample_unit_test_log_incident -->
<a id='snippet-sample_unit_test_log_incident'></a>
```cs
[Fact]
public void unit_test()
{
    var contact = new Contact(ContactChannel.Email);
    var command = new LogIncident(Guid.NewGuid(), contact, "It's broken", Guid.NewGuid());

    // Pure function FTW!
    var (response, startStream) = LogIncidentEndpoint.Post(command);
    
    // Should only have the one event
    startStream.Events.ShouldBe([
        new IncidentLogged(command.CustomerId, command.Contact, command.Description, command.LoggedBy)
    ]);
}
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Samples/IncidentService/IncidentService.Tests/when_logging_an_incident.cs#L16-L33' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_unit_test_log_incident' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

::: tip
Encouraging testability is something the "Critter Stack" community takes a lot of pride. I would
like to note that in many other event sourcing tools you can only effectively test command handlers
through end to end, integration tests
:::

## Creating an Integration Test Harness

::: info
This section was a request from a user, hope it makes sense. Alba is part of the same [JasperFx GitHub organization]()
as Wolverine and Marten. In case your curious, the company [JasperFx Software](https://github.com/JasperFx) was named after the
GitHub organization which in turn is named after one of our core team's ancestral hometown. 
:::

While we're definitely watching the [TUnit project](https://github.com/thomhurst/TUnit) and some of our customers happily use [NUnit](https://nunit.org/),
I'm going to use a combination of [xUnit.Net](https://xunit.net/) and the [JasperFx Alba project](https://jasperfx.github.io/alba/) to author
integration tests against our application. What I'm showing here is **a way** to do this, and certainly
not the only possible way to write integration tests.

My preference is to mostly use the application's `Program` bootstrapping with maybe just a few
overrides so that you are mostly using the application **as it is actually configured in production**.
As a little tip, I've added this bit of marker code to the very bottom of our `Program` file:

<!-- snippet: sample_Program_marker -->
<a id='snippet-sample_program_marker'></a>
```cs
// Adding this just makes it easier to bootstrap your
// application in a test harness project. Only a convenience
public partial class Program{}
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Samples/IncidentService/IncidentService/Program.cs#L68-L74' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_program_marker' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

Having that above, I'll switch to the test harness project and create a shared fixture to bootstrap
the `IHost` for the application throughout the integration tests:

<!-- snippet: sample_AppFixture_in_incident_service_testing -->
<a id='snippet-sample_appfixture_in_incident_service_testing'></a>
```cs
public class AppFixture : IAsyncLifetime
{
    public IAlbaHost? Host { get; private set; }

    public async Task InitializeAsync()
    {
        JasperFxEnvironment.AutoStartHost = true;

        // This is bootstrapping the actual application using
        // its implied Program.Main() set up
        Host = await AlbaHost.For<Program>(x =>
        {
            // Just showing that you *can* override service
            // registrations for testing if that's useful
            x.ConfigureServices(services =>
            {
                // If wolverine were using Rabbit MQ / SQS / Azure Service Bus,
                // turn that off for now
                services.DisableAllExternalWolverineTransports();
            });

        });
    }

    public async Task DisposeAsync()
    {
        await Host!.StopAsync();
        Host.Dispose();
    }
}
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Samples/IncidentService/IncidentService.Tests/IntegrationContext.cs#L14-L47' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_appfixture_in_incident_service_testing' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

And I like to add a base class for integration tests with some convenience methods that have
been useful here and there:

<!-- snippet: sample_IntegrationContext_for_integration_service -->
<a id='snippet-sample_integrationcontext_for_integration_service'></a>
```cs
[CollectionDefinition("integration")]
public class IntegrationCollection : ICollectionFixture<AppFixture>;

[Collection("integration")]
public abstract class IntegrationContext : IAsyncLifetime
{
    private readonly AppFixture _fixture;

    protected IntegrationContext(AppFixture fixture)
    {
        _fixture = fixture;
        Runtime = (WolverineRuntime)fixture.Host!.Services.GetRequiredService<IWolverineRuntime>();
    }

    public WolverineRuntime Runtime { get; }

    public IAlbaHost Host => _fixture.Host!;
    public IDocumentStore Store => _fixture.Host!.Services.GetRequiredService<IDocumentStore>();

    async Task IAsyncLifetime.InitializeAsync()
    {
        // Using Marten, wipe out all data and reset the state
        // back to exactly what we described in InitialAccountData
        await Store.Advanced.ResetAllData();
    }

    // This is required because of the IAsyncLifetime
    // interface. Note that I do *not* tear down database
    // state after the test. That's purposeful
    public Task DisposeAsync()
    {
        return Task.CompletedTask;
    }

    public Task<IScenarioResult> Scenario(Action<Scenario> configure)
    {
        return Host.Scenario(configure);
    }

    // This method allows us to make HTTP calls into our system
    // in memory with Alba, but do so within Wolverine's test support
    // for message tracking to both record outgoing messages and to ensure
    // that any cascaded work spawned by the initial command is completed
    // before passing control back to the calling test
    protected async Task<(ITrackedSession, IScenarioResult)> TrackedHttpCall(Action<Scenario> configuration)
    {
        IScenarioResult result = null!;

        // The outer part is tying into Wolverine's test support
        // to "wait" for all detected message activity to complete
        var tracked = await Host.ExecuteAndWaitAsync(async () =>
        {
            // The inner part here is actually making an HTTP request
            // to the system under test with Alba
            result = await Host.Scenario(configuration);
        });

        return (tracked, result);
    }
}
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Samples/IncidentService/IncidentService.Tests/IntegrationContext.cs#L49-L113' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_integrationcontext_for_integration_service' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

With all of that in place (and if you're using Docker for your infrastructure, a quick `docker compose up -d` command),
we can write an end to end test for the `LogIncident` endpoint like this:

<!-- snippet: sample_end_to_end_on_log_incident -->
<a id='snippet-sample_end_to_end_on_log_incident'></a>
```cs
[Fact]
public async Task happy_path_end_to_end()
{
    var contact = new Contact(ContactChannel.Email);
    var command = new LogIncident(Guid.NewGuid(), contact, "It's broken", Guid.NewGuid());
    
    // Log a new incident first
    var initial = await Scenario(x =>
    {
        x.Post.Json(command).ToUrl("/api/incidents");
        x.StatusCodeShouldBe(201);
    });

    // Read the response body by deserialization
    var response = initial.ReadAsJson<CreationResponse<Guid>>();

    // Reaching into Marten to build the current state of the new Incident
    // just to check the expected outcome
    using var session = Host.DocumentStore().LightweightSession();
    var incident = await session.Events.AggregateStreamAsync<Incident>(response.Value);
    
    incident.Status.ShouldBe(IncidentStatus.Pending);
}
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Samples/IncidentService/IncidentService.Tests/when_logging_an_incident.cs#L35-L61' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_end_to_end_on_log_incident' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

## Appending Events to an Existing Stream

::: info
Myself and others have frequently compared the "aggregate handler workflow" to the [Decider pattern](https://thinkbeforecoding.com/post/2021/12/17/functional-event-sourcing-decider)
from the functional programming community, and it is similar in intent, but we think the Wolverine
aggregate handler workflow does a better job of managing complexity and testability in non-trivial
projects than the "Decider" pattern that can easily devolve into being just a massive switch statement.
:::

This time let's write a simple HTTP endpoint to accept a `CategoriseIncident` command that may
decide to append a new event to an `Incident` event stream. For exactly this kind of command
handler, Wolverine has the [aggregate handler workflow](/guide/durability/marten/event-sourcing) that
allows you to express most command handlers that target Marten event sourcing as pure functions.

On to the code:

<!-- snippet: sample_CategoriseIncident -->
<a id='snippet-sample_categoriseincident'></a>
```cs
public record CategoriseIncident(
    IncidentCategory Category,
    Guid CategorisedBy,
    int Version
);

public static class CategoriseIncidentEndpoint
{
    // This is Wolverine's form of "Railway Programming"
    // Wolverine will execute this before the main endpoint,
    // and stop all processing if the ProblemDetails is *not*
    // "NoProblems"
    public static ProblemDetails Validate(Incident incident)
    {
        return incident.Status == IncidentStatus.Closed 
            ? new ProblemDetails { Detail = "Incident is already closed" } 
            
            // All good, keep going!
            : WolverineContinue.NoProblems;
    }
    
    // This tells Wolverine that the first "return value" is NOT the response
    // body
    [EmptyResponse]
    [WolverinePost("/api/incidents/{incidentId:guid}/category")]
    public static IncidentCategorised Post(
        // the actual command
        CategoriseIncident command, 
        
        // Wolverine is generating code to look up the Incident aggregate
        // data for the event stream with this id
        [Aggregate("incidentId")] Incident incident)
    {
        // This is a simple case where we're just appending a single event to
        // the stream.
        return new IncidentCategorised(incident.Id, command.Category, command.CategorisedBy);
    }
}
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Samples/IncidentService/IncidentService/CategoriseIncident.cs#L8-L49' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_categoriseincident' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

In this case, I'm sourcing the `Incident` value using the `incidentId` route argument as 
the identity with the [[Aggregate] attribute usage](/guide/http/marten.html#marten-aggregate-workflow) that's 
specific to the `WolverineFx.Http.Marten` usage. Behind the scenes, Wolverine is using
Marten's [`FetchForWriting` API](https://martendb.io/scenarios/command_handler_workflow.html#fetchforwriting).

It's ugly, but the full generated code from Wolverine is:

```csharp
    public class POST_api_incidents_incidentId_category : Wolverine.Http.HttpHandler
    {
        private readonly Wolverine.Http.WolverineHttpOptions _wolverineHttpOptions;
        private readonly Wolverine.Runtime.IWolverineRuntime _wolverineRuntime;
        private readonly Wolverine.Marten.Publishing.OutboxedSessionFactory _outboxedSessionFactory;

        public POST_api_incidents_incidentId_category(Wolverine.Http.WolverineHttpOptions wolverineHttpOptions, Wolverine.Runtime.IWolverineRuntime wolverineRuntime, Wolverine.Marten.Publishing.OutboxedSessionFactory outboxedSessionFactory) : base(wolverineHttpOptions)
        {
            _wolverineHttpOptions = wolverineHttpOptions;
            _wolverineRuntime = wolverineRuntime;
            _outboxedSessionFactory = outboxedSessionFactory;
        }



        public override async System.Threading.Tasks.Task Handle(Microsoft.AspNetCore.Http.HttpContext httpContext)
        {
            // Reading the request body via JSON deserialization
            var (command, jsonContinue) = await ReadJsonAsync<IncidentService.CategoriseIncident>(httpContext);
            if (jsonContinue == Wolverine.HandlerContinuation.Stop) return;
            var version = command.Version;
            var messageContext = new Wolverine.Runtime.MessageContext(_wolverineRuntime);
            if (!System.Guid.TryParse((string)httpContext.GetRouteValue("incidentId"), out var incidentId))
            {
                httpContext.Response.StatusCode = 404;
                return;
            }


            await using var documentSession = _outboxedSessionFactory.OpenSession(messageContext);
            var eventStore = documentSession.Events;
            var eventStream = await documentSession.Events.FetchForWriting<IncidentService.Incident>(incidentId, version,httpContext.RequestAborted);
            if (eventStream.Aggregate == null)
            {
                await Microsoft.AspNetCore.Http.Results.NotFound().ExecuteAsync(httpContext);
                return;
            }

            var problemDetails1 = IncidentService.CategoriseIncidentEndpoint.Validate(eventStream.Aggregate);
            // Evaluate whether the processing should stop if there are any problems
            if (!(ReferenceEquals(problemDetails1, Wolverine.Http.WolverineContinue.NoProblems)))
            {
                await WriteProblems(problemDetails1, httpContext).ConfigureAwait(false);
                return;
            }


            
            // The actual HTTP request handler execution
            var incidentCategorised = IncidentService.CategoriseIncidentEndpoint.Post(command, eventStream.Aggregate);

            eventStream.AppendOne(incidentCategorised);
            await documentSession.SaveChangesAsync(httpContext.RequestAborted).ConfigureAwait(false);
            // Wolverine automatically sets the status code to 204 for empty responses
            if (!httpContext.Response.HasStarted) httpContext.Response.StatusCode = 204;
        }

    }
```

The usage of the `FetchForWriting()` API under the covers sets us up for both appending the events
returned by our main command endpoint method to the right stream identified by the route argument. It's
also opting into [optimistic concurrency checks](https://en.wikipedia.org/wiki/Optimistic_concurrency_control) both at the time the current `Incident` state is fetched
and when the `IDocumentSession.SaveChangesAsync()` call is made. If you'll refer back to the `CategoriseIncident`
command type, you'll see that it has a `Version` property on it. By convention, Wolverine is going
to pipe that value in the command to the `FetchForWriting` API to facilitate the optimistic concurrency
checks.

::: info
There is also an option to use pessimistic locking through native PostgreSQL row locking, but please
be cautious with this usage as it can lead to worse performance by serializing requests and maybe dead lock issues. It's
probably more of a "break glass if necessary" approach. 
:::

You'll also notice that the HTTP endpoint above is broken up into two methods, the main `Post()` method
and a `Validate()` method. As the names imply, Wolverine will call the `Validate()` method first as a 
filter to decide whether or not to proceed on to the main method. If the `Validate()` returns a `ProblemDetails`
that actually contains problems, that stops the processing with a 400 HTTP status code and writes out the
`ProblemDetails` to the response. This is part of Wolverine's [compound handler](/guide/handlers/#compound-handlers) technique that acts as
a sort of [Railway Programming technique](./railway-programming) for Wolverine. You can learn more about Wolverine's 
built in support for [ProblemDetails here](/guide/http/problemdetails).

::: tip
Wolverine.HTTP is able to glean more OpenAPI metadata from the signatures of the `Validate` methods
that return `ProblemDetails`. Moreover, by using these validate methods to handle validation concerns and
"sad path" failures, you're much more likely to be able to just return the response body directly from
the endpoint method -- which also helps Wolverine.HTTP be able to generate OpenAPI metadata from the type
signatures without forcing you to clutter up your code with more attributes just for OpenAPI.
:::

Now, back to the `FetchForWriting` API usage. Besides the support for concurrency protection, `FetchForWriting` wallpapers over which projection
lifecycle you're using to give you the compiled `Incident` data for a single stream. In the absence
of any other configuration, Marten is building it `Live`, which means that inside of the call to
`FetchForWriting`, Marten is fetching all the raw events for the `Incident` stream and running those
through the implied [single stream projection](https://martendb.io/events/projections/aggregate-projections.html#aggregate-by-stream) of the `Incident` type to give you the latest information
that is then passed into your endpoint method as just an argument. 

Now though, unlike many other Event Sourcing tools, Marten can reliably support "snapshotting" of
the aggregate data and you can use that to improve performance in your CQRS command handlers. To 
make that concrete, let's go back to our `Program` file where we're bootstrapping Marten
and we're going to add this code to update the `Incident` aggregates `Inline` with event capture:

```csharp
builder.Services.AddMarten(opts =>
{
    var connectionString = builder.Configuration.GetConnectionString("Marten");
    opts.Connection(connectionString);
    opts.DatabaseSchemaName = "incidents";
    
    opts.Projections.Snapshot<Incident>(SnapshotLifecycle.Inline);

    // Recent optimization you'd want with FetchForWriting up above
    opts.Projections.UseIdentityMapForAggregates = true;
})
    
// Another performance optimization if you're starting from
// scratch
.UseLightweightSessions()

// This adds configuration with Wolverine's transactional outbox and
// Marten middleware support to Wolverine
.IntegrateWithWolverine();
```

::: tip
The `Json.WriteById()` API is in the [Marten.AspNetCore Nuget](https://martendb.io/documents/aspnetcore). 
:::

In this usage, the `Incident` projection gets updated every single time you append events,
so that you can load the current data straight out of the database and know it's consistent
with the event state. Switching to the "read side", if you are using `Inline` as the projection
lifecycle, we can write a `GET` endpoint for a single `Incident` like this:

```csharp
public static class GetIncidentEndpoint
{
    // For right now, you have to help out the OpenAPI metdata
    [Produces(typeof(Incident))]
    [WolverineGet("/api/incidents/{id}")]
    public static async Task Get(Guid id, IDocumentSession session, HttpContext context)
    {
        await session.Json.WriteById<Incident>(id, context);
    }
}
```

The code up above is very efficient as all it's doing is taking the raw JSON stored in PostgreSQL
and streaming it byte by byte right down to the HTTP response. No deserialization to the `Incident`
.NET type just to immediately serialize it to a string, then writing it down. Of course this does
require you to make your Marten JSON serialization settings exactly match what your clients want,
but that's perfectly possible. 

If we decide to use `Live` or `Async` aggregation with [Marten's Async Daemon](https://martendb.io/events/projections/async-daemon.html) functionality,
you could change the `GET` endpoint to this to ensure that you have the right state that matches
the current event stream:

```csharp
public static class GetIncidentEndpoint
{
    // For right now, you have to help out the OpenAPI metdata
    [WolverineGet("/api/incidents/{id}")]
    public static async Task<Incident?> Get(
        Guid id, 
        IDocumentSession session, 
        
        // This will be the HttpContext.RequestAborted
        CancellationToken token)
    {
        return await session.Events.FetchLatest<Incident>(id, token);
    }
}
```

The `Events.FetchLatest()` API in Marten will also wallpaper over the actual projection lifecycle
of the `Incident` projection, but does it in a lighter weight "read only" way compared to `FetchForWriting()`.

## Publishing or Handling Events

With an [Event Driven Architecture](https://learn.microsoft.com/en-us/azure/architecture/guide/architecture-styles/event-driven) approach, 
you may want to do work against the events that are persisted to Marten. You can always explicitly publish messages through Wolverine
at the same time you are appending events, but what if it's just easier to use the events themselves as messages to other message handlers
or even to other services?

The Wolverine + Marten combination comes with two main ways to do exactly that:

[Event Forwarding](/guide/durability/marten/event-forwarding) is a lightweight way to immediately publish events that are appended to
Marten within a Wolverine message handler through Wolverine's messaging infrastructure. Events can be handled either in process through
local queues or published to external message brokers depending on the [message routing subscriptions](/guide/messaging/subscriptions) for that event type. Just
note that event forwarding comes with **no ordering guarantees**.

[Event Subscriptions](/guide/durability/marten/subscriptions) utilizes a **strictly ordered mechanism** to read in and process event data from the Marten event store.
Wolverine supports three modes of event subscriptions from Marten:

1. Executing each event with a known Wolverine message handler (either the event type itself or wrapped in the Marten `IEvent<T>` envelope)
   in strict order. This is essentially just calling [`IMessageBus.InvokeAsync()`](/guide/messaging/message-bus.html#invoking-message-execution) event by event in strict order from the Marten event store. 
2. Publishing the events as messages through Wolverine. Essentially calling [`IMessageBus.PublishAsync()`](/guide/messaging/message-bus.html#sending-or-publishing-messages) on each event in strict order.
3. User defined operations on a batch of events at a time, again in strict order that the events are appended to the Marten event store.

In all cases, the Event Subscriptions are running in a background process managed either by Marten itself with its [Async Daemon](/events/projections/async-daemon)
or the [Projection/Subscription Distribution](/guide/durability/marten/distribution) feature in Wolverine. 

## Scaling Marten Projections

::: info
The feature in this section was originally intended to be a commercial add on, but we decided to 
pull it into Wolverine core.
:::

Wolverine has the ability to distribute the asynchronous projections and subscriptions to Marten events evenly across
an application cluster for better scalability. See [Projection/Subscription Distribution](/guide/durability/marten/distribution) for more information.

## Observability

Both Marten and Wolverine have strong support for [OpenTelemetry](https://opentelemetry.io/) (Otel) tracing as well as emitting performance 
metrics that can be used in conjunction with tools like Prometheus or Grafana to monitor and troubleshoot systems in 
production. 

See [Wolverine's Otel and Metrics](/guide/logging.html#open-telemetry) support and [Marten's Otel and Metrics](https://martendb.io/otel.html#open-telemetry-and-metrics) support for more information.
