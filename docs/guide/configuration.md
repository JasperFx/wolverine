# Configuration

::: info
As of 3.0,  Wolverine **does not require the usage of the [Lamar](https://jasperfx.github.io/lamar) IoC container**, and will no longer replace the built in .NET container with Lamar.

Wolverine 3.0 *is* tested with both the built in `ServiceProvider` and Lamar. It's theoretically possible to use other
IoC containers now as long as they conform to the .NET conforming container, but this isn't tested by the Wolverine team.
:::

Wolverine is configured with the `IHostBuilder.UseWolverine()` or `HostApplicationBuilder` extension methods, with the actual configuration
living on a single `WolverineOptions` object. The `WolverineOptions` is the configuration model for your Wolverine application,
and as such it can be used to configure directives about:

* Basic elements of your Wolverine system like the system name itself
* Connections to [external messaging infrastructure](/guide/messaging/introduction) through Wolverine's *transport* model
* Messaging endpoints for either listening for incoming messages or subscribing endpoints
* [Subscription rules](/guide/messaging/subscriptions) for outgoing messages
* How [message handlers](/guide/messages) are discovered within your application and from what assemblies
* Policies to control how message handlers function, or endpoints are configured, or error handling policies

![Wolverine Configuration Model](/configuration-model.png)

::: info
At this point, Wolverine only supports [IHostBuilder](https://learn.microsoft.com/en-us/dotnet/api/microsoft.extensions.hosting.ihostbuilder?view=dotnet-plat-ext-7.0) for bootstrapping, but may also support the newer [HostApplicationBuilder](https://learn.microsoft.com/en-us/dotnet/api/microsoft.extensions.hosting.hostapplicationbuilder?view=dotnet-plat-ext-7.0)
model in the future.
:::

## With ASP.NET Core

::: info
Do note that there's some [additional configuration to use WolverineFx.HTTP](/guide/http/integration) as well.
:::

Below is a sample of adding Wolverine to an ASP.NET Core application that is bootstrapped with
`WebApplicationBuilder`:

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

## "Headless" Applications

:::tip
The `WolverineOptions.Services` property can be used to add additional IoC service registrations with
either the standard .NET `IServiceCollection` model syntax.
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

    // Executing with Oakton as the command line parser to unlock
    // quite a few utilities and diagnostics in our Wolverine application
    .RunOaktonCommands(args);
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Testing/OpenTelemetry/Subscriber1/Program.cs#L10-L46' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_bootstrapping_headless_service' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

As of Wolverine 3.0, you can also use the `HostApplicationBuilder` mechanism as well:

<!-- snippet: sample_bootstrapping_with_auto_apply_transactions_for_sql_server -->
<a id='snippet-sample_bootstrapping_with_auto_apply_transactions_for_sql_server'></a>
```cs
var builder = Host.CreateApplicationBuilder();
builder.UseWolverine(opts =>
{
    var connectionString = builder.Configuration.GetConnectionString("database");

    opts.Services.AddDbContextWithWolverineIntegration<SampleDbContext>(x =>
    {
        x.UseSqlServer(connectionString);
    });

    opts.UseEntityFrameworkCoreTransactions();

    // Add the auto transaction middleware attachment policy
    opts.Policies.AutoApplyTransactions();
});

using var host = builder.Build();
await host.StartAsync();
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Persistence/EfCoreTests/SampleUsageWithAutoApplyTransactions.cs#L13-L34' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_bootstrapping_with_auto_apply_transactions_for_sql_server' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

And lastly, you can just use `IServiceCollection.AddWolverine()` by itself.

## Replacing ServiceProvider with Lamar

If you run into any trouble whatsoever with code generation after upgrading to Wolverine 3.0, please:

1. Please [raise a GitHub issue in Wolverine](https://github.com/JasperFx/wolverine/issues/new/choose) with some description of the offending message handler or http endpoint
2. Fall back to Lamar for your IoC tool

To use Lamar, add this Nuget to your main project:

```bash
dotnet add package Lamar.Microsoft.DependencyInjection
```

If you're using `IHostBuilder` like you might for a simple console app, it's:

<!-- snippet: sample_use_lamar_with_host_builder -->
<a id='snippet-sample_use_lamar_with_host_builder'></a>
```cs
// With IHostBuilder
var builder = Host.CreateDefaultBuilder();
builder.UseLamar();
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Testing/CoreTests/Configuration/DocumentationSamples.cs#L14-L20' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_use_lamar_with_host_builder' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

In a web application, it's:

```csharp
var builder = WebApplication.CreateBuilder(args);
builder.Host.UseLamar();
```

and with `HostApplicationBuilder`, try:

```csharp
var builder = Host.CreateApplicationBuilder();

// Little ugly, and Lamar *should* have a helper for this...
builder.ConfigureContainer<ServiceRegistry>(new LamarServiceProviderFactory());
```

