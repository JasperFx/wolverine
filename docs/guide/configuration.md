# Configuration

::: warning
Wolverine requires the usage of the [Lamar](https://jasperfx.github.io/lamar) IoC container, and the call
to `UseWolverine()` quietly replaces the built in .NET container with Lamar.

Lamar was originally written specifically to support Wolverine's runtime model as well as to be a higher performance
replacement for the older StructureMap tool.
:::

Wolverine is configured with the `IHostBuilder.UseWolverine()` extension methods, with the actual configuration
living on a single `WolverineOptions` object.

## With ASP.NET Core

Below is a sample of adding Wolverine to an ASP.NET Core application that is bootstrapped with
`WebApplicationBuilder`:

<!-- snippet: sample_Quickstart_Program -->
<a id='snippet-sample_quickstart_program'></a>
```cs
using Oakton;
using Quickstart;
using Wolverine;

var builder = WebApplication.CreateBuilder(args);

// For now, this is enough to integrate Wolverine into
// your application, but there'll be *much* more
// options later of course :-)
builder.Host.UseWolverine();

// Some in memory services for our application, the
// only thing that matters for now is that these are
// systems built by the application's IoC container
builder.Services.AddSingleton<UserRepository>();
builder.Services.AddSingleton<IssueRepository>();

var app = builder.Build();

// An endpoint to create a new issue
app.MapPost("/issues/create", (CreateIssue body, IMessageBus bus) => bus.InvokeAsync(body));

// An endpoint to assign an issue to an existing user
app.MapPost("/issues/assign", (AssignIssue body, IMessageBus bus) => bus.InvokeAsync(body));

// Opt into using Oakton for command line parsing
// to unlock built in diagnostics and utility tools within
// your Wolverine application
return await app.RunOaktonCommands(args);
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Samples/Quickstart/Program.cs#L1-L33' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_quickstart_program' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

## "Headless" Applications

:::tip
The `WolverineOptions.Services` property can be used to add additional IoC service registrations with
either the standard .NET `IServiceCollection` model or the [Lamar ServiceRegistry](https://jasperfx.github.io/lamar/guide/ioc/registration/registry-dsl.html) syntax.
:::

For "headless" console applications with no user interface or HTTP service endpoints, the bootstrapping
can be done with just the `HostBuilder` mechanism as shown below:

<!-- snippet: sample_bootstrapping_headless_service -->
<a id='snippet-sample_bootstrapping_headless_service'></a>
```cs
return await Host.CreateDefaultBuilder(args)
    .UseWolverine(opts =>
    {
        opts.ServiceName = "Subscriber1";

        opts.Discovery.DisableConventionalDiscovery().IncludeType<Subscriber1Handlers>();

        opts.ListenAtPort(MessagingConstants.Subscriber1Port);

        opts.UseRabbitMq().AutoProvision();

        opts.ListenToRabbitQueue(MessagingConstants.Subscriber1Queue);

        // Publish to the other subscriber
        opts.PublishMessage<RabbitMessage2>().ToRabbitQueue(MessagingConstants.Subscriber2Queue);

        // Add Open Telemetry tracing
        opts.Services.AddOpenTelemetryTracing(builder =>
        {
            builder
                .SetResourceBuilder(ResourceBuilder
                    .CreateDefault()
                    .AddService("Subscriber1"))
                .AddJaegerExporter()

                // Add Wolverine as a source
                .AddSource("Wolverine");
        });
    })
    .RunOaktonCommands(args);
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Testing/OpenTelemetry/Subscriber1/Program.cs#L10-L43' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_bootstrapping_headless_service' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->
