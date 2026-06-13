# Diagnostics

Wolverine can be configuration intensive, allows for quite a bit of customization if you want to go down that road, and involves
quite a bit of external infrastructure. All of those things can be problematic, so Wolverine tries to provide diagnostic tools
to unwind what's going on inside the application and the application's configuration. 

Many of the diagnostics explained in this page are part of the [JasperFx command line integration](/guide/command-line). As a reminder,
to utilize this command line integration, you need to apply JasperFx as your command line parser as shown in the last line of the quickstart
sample `Program.cs` file:

<!-- snippet: sample_quickstart_program -->
<a id='snippet-sample_quickstart_program'></a>
```cs
using JasperFx;
using Quickstart;
using Wolverine;

var builder = WebApplication.CreateBuilder(args);

// The almost inevitable inclusion of OpenApi:)
builder.Services.AddOpenApi();

// For now, this is enough to integrate Wolverine into
// your application, but there'll be *many* more
// options later of course :-)
builder.Host.UseWolverine();

// Some in memory services for our application, the
// only thing that matters for now is that these are
// systems built by the application's IoC container
builder.Services.AddSingleton<UserRepository>();
builder.Services.AddSingleton<IssueRepository>();

var app = builder.Build();

// An endpoint to create a new issue that delegates to Wolverine as a mediator
app.MapPost("/issues/create", (CreateIssue body, IMessageBus bus) => bus.InvokeAsync(body));

// An endpoint to assign an issue to an existing user that delegates to Wolverine as a mediator
app.MapPost("/issues/assign", (AssignIssue body, IMessageBus bus) => bus.InvokeAsync(body));

app.MapOpenApi();

app.MapGet("/", () => Results.Redirect("/swagger"));

// Opt into using JasperFx for command line parsing
// to unlock built in diagnostics and utility tools within
// your Wolverine application
return await app.RunJasperFxCommands(args);
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Samples/Quickstart/Program.cs#L1-L39' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_quickstart_program' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

## Command Line Description

From the command line at the root of your project, you can get a textual report about your Wolverine application
including discovered handlers, messaging endpoints, and error handling through this command:

```bash
dotnet run -- describe
```

## Previewing Generated Code

If you ever have any question about the applicability of Wolverine (or custom) conventions or the middleware that
is configured for your application, you can see the exact code that Wolverine generates around your messaging handlers
or HTTP endpoint methods from the command line.

To write out all the generated source code to the `/Internal/Generated/WolverineHandlers` folder of your application (or designated application assembly),
use this command:

```bash
dotnet run -- codegen write
```

The naming convention for the files is `[Message Type Name]Handler#######` where the numbers are just a hashed suffix to disambiguate
message types with the same name, but in different namespaces.

Or if you just want to preview the code into your terminal window, you can also say:

```bash
dotnet run -- codegen preview
```

## Environment Checks

::: info
Wolverine 4.0 will embrace the new [IHealthCheck](https://learn.microsoft.com/en-us/dotnet/api/microsoft.extensions.diagnostics.healthchecks.ihealthcheck?view=net-8.0) model in .NET as a replacement for the older, JasperFx-centric
environment check model in this section. 
:::

Wolverine's external messaging transports and the durable inbox/outbox support expose [Oakton's environment checks](https://jasperfx.github.io/oakton/guide/host/environment.html)
facility to help make your Wolverine applications be self diagnosing on configuration or connectivity issues like:

* Can the application connect to its configured database?
* Can the application connect to its configured Rabbit MQ / Amazon SQS / Azure Service Bus message brokers?
* Is the underlying IoC container registrations valid?

To exercise this functionality, try:

```bash
dotnet run -- check-env
```

Or even at startup, you can use:

```bash
dotnet run -- run --check
```

to have the environment checks executed at application startup, but just realize that the application will shutdown if any
checks fail.

## Troubleshooting Handler Discovery

Wolverine has admittedly been a little challenging for some new users to get used to its handler discovery. If you are not seeing
Wolverine discover and use a message handler type and method, try this mechanism temporarily so that Wolverine can
try to explain why it's not picking that type and method up as a message handler:

<!-- snippet: sample_describe_handler_match -->
<a id='snippet-sample_describe_handler_match'></a>
```cs
using var host = await Host.CreateDefaultBuilder()
    .UseWolverine(opts =>
    {
        // Surely plenty of other configuration for Wolverine...

        // This *temporary* line of code will write out a full report about why or
        // why not Wolverine is finding this handler and its candidate handler messages
        Console.WriteLine(opts.DescribeHandlerMatch(typeof(MyMissingMessageHandler)));
    }).StartAsync();
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Samples/DocumentationSamples/HandlerDiscoverySamples.cs#L140-L151' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_describe_handler_match' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

You can get the same report from the command line without editing any code, using the
[`describe-handlers`](/guide/command-line#describe-handlers) diagnostics command <Badge type="tip" text="6.0" />:

```bash
dotnet run -- wolverine-diagnostics describe-handlers MyMissingMessageHandler
```

The type name is fuzzy-matched against the types in your application, so if more than one type matches you
get a report for each. The command builds the host and compiles the handler graph without starting it, so
no database or broker connection is required.

## Troubleshooting Message Routing

Among other information, you can find a preview of how Wolverine will route known message types through the command line
with:

```bash
dotnet run -- describe
```

Part of this output is a table of the known message types and the routed destination of any subscriptions. You can enhance
this diagnostic by helping Wolverine to [discover message types](/guide/messages#message-discovery) in your system. 

And lastly, there's a programmatic way to "preview" the Wolverine message routing at runtime that might 
be helpful:

<!-- snippet: sample_using_preview_subscriptions -->
<a id='snippet-sample_using_preview_subscriptions'></a>
```cs
private static void using_preview_subscriptions(IMessageBus bus)
{
    // Preview where Wolverine is wanting to send a message
    var outgoing = bus.PreviewSubscriptions(new BlueMessage());
    foreach (var envelope in outgoing)
    {
        // The URI value here will identify the endpoint where the message is
        // going to be sent (Rabbit MQ exchange, Azure Service Bus topic, Kafka topic, local queue, etc.)
        Debug.WriteLine(envelope.Destination);
    }
}
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Testing/CoreTests/Runtime/Routing/routing_rules.cs#L102-L115' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_using_preview_subscriptions' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

### Explaining *Why* a Message Routes Where It Does <Badge type="tip" text="6.0" />

`PreviewSubscriptions` tells you *where* a message goes; `IWolverineRuntime.ExplainRoutingFor(Type)`
tells you *why*. It returns a structured `RoutingExplanation` that walks Wolverine's route source
chain in order and records what each source did:

```csharp
var runtime = host.Services.GetRequiredService<IWolverineRuntime>();

RoutingExplanation explanation = runtime.ExplainRoutingFor(typeof(CreateOrder));

// Human- and AI-readable text rendering
Console.WriteLine(explanation.ToText());

// Or inspect the structure directly
foreach (var step in explanation.Steps)
{
    // step.Source is a RouteSourceDescriptor (Name, Description, IsAdditive, Conventions)
    if (step.SkipReason is not null)
    {
        // an earlier *terminating* source already produced routes, so this one never ran
        Console.WriteLine($"{step.Source.Name}: skipped — {step.SkipReason}");
    }
    else
    {
        Console.WriteLine($"{step.Source.Name}: produced {step.Produced.Count} route(s)");
    }
}
```

Wolverine consults its route sources in a fixed order, and a **terminating** source (e.g.
`ExplicitRouting`, `AgentCommands`) short-circuits the rest of the chain once any routes have been
accumulated, while **additive** sources (e.g. `LocalRouting`, `ConventionalRouting`) let later
sources keep contributing. `RoutingExplanation` makes that precedence visible:

- `MessageType` and `IsSystemMessageType` — the latter is `true` for framework-internal types
  (`IInternalMessage` / `IAgentCommand` / `INotToBeRouted`, or types from an
  `[ExcludeFromServiceCapabilities]` assembly), which are filtered out of observers and service
  capabilities.
- `LocalRoutingConventionDisabled` — the current value of
  `WolverineOptions.LocalRoutingConventionDisabled`; when `true` the `LocalRouting` source is
  short-circuited, so it explains why a locally-handled message routes nowhere locally.
- `Steps` — one `RouteSourceStep` per route source consulted, in order, each carrying the source's
  descriptor, the routes it produced, and a `SkipReason` when a prior terminating source had
  already short-circuited the chain.
- `FinalRoutes` — the final, de-duplicated route set actually used.

The same information is also available from the command line — see
[`describe-routing --explain`](/guide/command-line#describe-routing) — and the route-source
descriptors are surfaced in Wolverine's service capabilities so external tooling (e.g. CritterWatch)
can reason about routing decisions. The text output is deliberately stable and labeled so it is
useful for both humans and AI agents.

