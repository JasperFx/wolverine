# Diagnostics

Wolverine can be configuration intensive, allows for quite a bit of customization if you want to go down that road, and involves
quite a bit of external infrastructure. All of those things can be problematic, so Wolverine tries to provide diagnostic tools
to unwind what's going on inside the application and the application's configuration. 

Many of the diagnostics explained in this page are part of the [Oakton command line integration](https://jasperfx.github.io/oakton). As a reminder,
to utilize this command line integration, you need to apply Oakton as your command line parser as shown in the last line of the quickstart
sample `Program.cs` file:

<!-- snippet: sample_Quickstart_Program -->
<a id='snippet-sample_quickstart_program'></a>
```cs
using JasperFx;
using Quickstart;
using Wolverine;

var builder = WebApplication.CreateBuilder(args);

// The almost inevitable inclusion of Swashbuckle:)
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

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

// Swashbuckle inclusion
app.UseSwagger();
app.UseSwaggerUI();

app.MapGet("/", () => Results.Redirect("/swagger"));

// Opt into using JasperFx for command line parsing
// to unlock built in diagnostics and utility tools within
// your Wolverine application
return await app.RunJasperFxCommands(args);
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Samples/Quickstart/Program.cs#L1-L43' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_quickstart_program' title='Start of snippet'>anchor</a></sup>
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
dotnet run -- check-env
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
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Samples/DocumentationSamples/HandlerDiscoverySamples.cs#L148-L160' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_describe_handler_match' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

## Asserting Wolverine Configuration

Probably mostly for testing projects, you can verify that all the message handlers and the underlying Lamar IoC container for your
application are in a valid state by executing this method:

<!-- snippet: sample_using_AssertWolverineConfigurationIsValid -->
<a id='snippet-sample_using_assertwolverineconfigurationisvalid'></a>
```cs
public static void assert_configuration_is_valid(IHost host)
{
    host.AssertWolverineConfigurationIsValid();
}
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Samples/DocumentationSamples/DiagnosticSamples.cs#L8-L15' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_using_assertwolverineconfigurationisvalid' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

Note that this method will attempt to generate and compile the source code for each message type and use [Lamar's own
diagnostics](https://jasperfx.github.io/lamar/guide/ioc/diagnostics/) as well.

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
public static void using_preview_subscriptions(IMessageBus bus)
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
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Testing/CoreTests/Runtime/Routing/routing_rules.cs#L90-L104' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_using_preview_subscriptions' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

