# Logging, Diagnostics, and Metrics

Wolverine logs through the standard .NET `ILogger` abstraction, and there's nothing special you need to do
to enable that logging other than using one of the standard approaches for bootstrapping a .NET application
using `IHostBuilder`. Wolverine is logging all messages sent, received, and executed inline.

## Log Message Execution Start

Wolverine is absolutely meant for "grown up development," so there's a few options for logging and instrumentation. While Open Telemetry logging 
is built in and will always give you the activity span for message execution start and finish, you may want the start of each
message execution to be logged as well. Rather than force your development teams to write repetitive logging statements for every single
message handler method, you can ask Wolverine to do that for you:

<!-- snippet: sample_log_message_starting -->
<a id='snippet-sample_log_message_starting'></a>
```cs
using var host = await Host.CreateDefaultBuilder()
    .UseWolverine(opts =>
    {
        // Opt into having Wolverine add a log message at the beginning
        // of the message execution
        opts.Policies.LogMessageStarting(LogLevel.Information);
    }).StartAsync();
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Samples/DocumentationSamples/LoggingUsage.cs#L11-L21' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_log_message_starting' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

With only the defaults, Wolverine is logging the type of message and the message id. As shown in the next section, you can also add
additional context to these log messages.

In conjunction with the "audited members" that are added to these logging statements, all the logging in Wolverine is using structural logging
for better searching within your logs. 

## Contextual Logging with Audited Members

::: warning
Be cognizant of the information you're writing to log files or Open Telemetry data and whether or not that data
is some kind of protected data like personal data identifiers.
:::

Wolverine gives you the ability to mark public fields or properties on message types as "audited members" that will be
part of the logging messages at the beginning of message execution described in the preview section, and also in the Open Telemetry support described in the
next section.

To explicitly mark members as "audited", you *can* use attributes within your message types (and these are inherited) like so:

<!-- snippet: sample_using_audit_attribute -->
<a id='snippet-sample_using_audit_attribute'></a>
```cs
public class AuditedMessage
{
    [Audit]
    public string Name { get; set; }

    [Audit("AccountIdentifier")] public int AccountId;
}
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Testing/CoreTests/Configuration/auditing_determination.cs#L90-L100' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_using_audit_attribute' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

Or if you are okay using a common message interface for common identification like "this message targets an account/organization/tenant/client"
like the `IAccountCommand` shown below:

<!-- snippet: sample_account_message_for_auditing -->
<a id='snippet-sample_account_message_for_auditing'></a>
```cs
// Marker interface
public interface IAccountMessage
{
    public int AccountId { get; }
}

// A possible command that uses our marker interface above
public record DebitAccount(int AccountId, decimal Amount) : IAccountMessage;
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Testing/CoreTests/Configuration/auditing_determination.cs#L118-L129' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_account_message_for_auditing' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

You can specify audited members through this syntax:

<!-- snippet: sample_explicit_registration_of_audit_properties -->
<a id='snippet-sample_explicit_registration_of_audit_properties'></a>
```cs
// opts is WolverineOptions inside of a UseWolverine() call
opts.Policies.ForMessagesOfType<IAccountMessage>().Audit(x => x.AccountId);
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Testing/CoreTests/Configuration/auditing_determination.cs#L77-L82' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_explicit_registration_of_audit_properties' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

## Open Telemetry

Wolverine also supports the [Open Telemetry](https://opentelemetry.io/docs/instrumentation/net/) standard for distributed tracing. To enable
the collection of Open Telemetry data, you need to add Wolverine as a data source as shown in this
code sample:

<!-- snippet: sample_enabling_open_telemetry -->
<a id='snippet-sample_enabling_open_telemetry'></a>
```cs
// builder.Services is an IServiceCollection object
builder.Services.AddOpenTelemetryTracing(x =>
{
    x.SetResourceBuilder(ResourceBuilder
            .CreateDefault()
            .AddService("OtelWebApi")) // <-- sets service name
        .AddJaegerExporter()
        .AddAspNetCoreInstrumentation()

        // This is absolutely necessary to collect the Wolverine
        // open telemetry tracing information in your application
        .AddSource("Wolverine");
});
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Testing/OpenTelemetry/OtelWebApi/Program.cs#L36-L52' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_enabling_open_telemetry' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

## Message Correlation

::: tip
Each individual message transport technology like Rabbit MQ, Azure Service Bus, or Amazon SQS has its own flavor of *Envelope Wrapper*, but Wolverine
uses its own `Envelope` structure internally and maps between its canonical representation and the transport specific envelope wrappers at runtime.
:::

As part of Wolverine's instrumentation, it tracks the causality between messages received and published by Wolverine. It also enables you to correlate Wolverine
activity back to inputs from outside of Wolverine like ASP.Net Core request ids. The key item here is Wolverine's `Envelope` class (see the [Envelope Wrapper](https://www.enterpriseintegrationpatterns.com/patterns/messaging/EnvelopeWrapper.html) pattern discussed in the venerable Enterprise Integration Patterns) that holds messages
the message and all the metadata for the message within Wolverine handling. 

| Property       | Type                | Source                                                           | Description                                                                              |
|----------------|---------------------|------------------------------------------------------------------|------------------------------------------------------------------------------------------|
| Id             | `Guid` (Sequential) | Assigned by Wolverine                                            | Identifies a specific Wolverine message                                                  |
| CorrelationId  | `string`            | See the following discussion                                     | Correlating identifier for the logical workflow or system action across multiple actions |
| ConversationId | `Guid`              | Assigned by Wolverine                                            | Id of the immediate message or workflow that caused this envelope to be sent             |
| SagaId         | `string`            | Assigned by Wolverine                                            | Identifies the current stateful saga that this message refers to, if part of a stateful saga |
| TenantId       | `string`            | Assigned by user on IMessageBus, but transmitted across messages | User defined tenant identifier for multi-tenancy strategies |

Correlation is a little bit complicated. The correlation id is originally owned at the `IMessageBus` or `IMessageContext` level. By default,
the `IMessageBus.CorrelationId` is set to be the [root id of the current System.Diagnostics.Activity](https://learn.microsoft.com/en-us/dotnet/api/system.diagnostics.activity.rootid?view=net-7.0#system-diagnostics-activity-rootid).
That's convenient, because it would hopefully, automatically tie your Wolverine behavior to outside activity like ASP.Net Core HTTP requests. 

If you are publishing messages within the context of a Wolverine handler -- either with `IMessageBus` / `IMessageContext` or through cascading messages -- the correlation id of any outgoing
messages will be the correlation id of the original message that is being currently handled. 

If there is no existing correlation id from either a current activity or a previous message, Wolverine will assign a new correlation id
as a `Guid` value converted to a string.


## Metrics

Wolverine is automatically tracking several performance related metrics through the [System.Diagnostics.Metrics](https://learn.microsoft.com/en-us/dotnet/api/system.diagnostics.metrics?view=net-8.0) types, 
which sets Wolverine users up for being able to export their system’s performance metrics to third party observability tools like Honeycomb or Datadog that support Open Telemetry metrics. The current set of metrics in Wolverine are shown below:


| Metric Name                  | Metric Type                                                                                               | Description                                                                                                                                                                                                                                                                            |
|------------------------------|-----------------------------------------------------------------------------------------------------------|----------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------|
| wolverine-messages-sent      | [Counter](https://opentelemetry.io/docs/reference/specification/metrics/api/#counter)                     | Number of messages sent                                                                                                                                                                                                                                                                |
| wolverine-execution-time     | [Histogram](https://opentelemetry.io/docs/reference/specification/metrics/api/#histogram)                 | Execution time in milliseconds                                                                                                                                                                                                                                                         |
| wolverine-messages-succeeded | Counter                                                                                                   | Number of messages successfully processed                                                                                                                                                                                                                                              |
| wolverine-dead-letter-queue  | Counter                                                                                                   | Number of messages moved to dead letter queues                                                                                                                                                                                                                                         |
| wolverine-effective-time     | Histogram                                                                                                 | Effective time between a message being sent and being completely handled in milliseconds. Right now this works between Wolverine to Wolverine application sending and from NServiceBus applications sending to Wolverine applications through Wolverine’s NServiceBus interoperability. |
| wolverine-execution-failure  | Counter                                                                                                   | Number of message execution failures. Tagged by exception type                                                                                                                                                                                                                         |
| wolverine-inbox-count        | [Observable Gauge](https://opentelemetry.io/docs/reference/specification/metrics/api/#asynchronous-gauge) | Samples the number of pending envelopes in the durable inbox (likely to change)                                                                                                                                                                                                        |
| wolverine-outbox-count       | Observable Gauge                                                                                          | Samples the number of pending envelopes in the durable outbox (likely to change)                                                                                                                                                                                                       |
| wolverine-scheduled-count    | Observable Gauge                                                                                          | Samples the number of pending scheduled envelopes in the durable inbox (likely to change)                                                                                                                                                                                              |

As a sample set up for publishing metrics, here's a proof of concept built with Honeycomb as the metrics collector:

```csharp
var host = Host.CreateDefaultBuilder(args)
    .UseWolverine((context, opts) =>
    {
        opts.ServiceName = "Metrics";
 
        // Open Telemetry *should* cover this anyway, but
        // if you want Wolverine to log a message for *beginning*
        // to execute a message, try this
        opts.Policies.LogMessageStarting(LogLevel.Debug);
         
        // For both Open Telemetry span tracing and the "log message starting..."
        // option above, add the AccountId as a tag for any command that implements
        // the IAccountCommand interface
        opts.Policies.ForMessagesOfType<IAccountCommand>().Audit(x => x.AccountId);
         
        // Setting up metrics and Open Telemetry activity tracing
        // to Honeycomb
        var honeycombOptions = context.Configuration.GetHoneycombOptions();
        honeycombOptions.MetricsDataset = "Wolverine:Metrics";
         
        opts.Services.AddOpenTelemetry()
            // enable metrics
            .WithMetrics(x =>
            {
                // Export metrics to Honeycomb
                x.AddHoneycomb(honeycombOptions);
            })
             
            // enable Otel span tracing
            .WithTracing(x =>
            {
                x.AddHoneycomb(honeycombOptions);
                x.AddSource("Wolverine");
            });
 
    })
    .UseResourceSetupOnStartup()
    .Build();
 
await host.RunAsync();
```

### Additional Metrics Tags

You can add additional tags to the performance metrics per message type for system specific correlation in tooling like Datadog, Grafana, or Honeycomb. From
an example use case that I personally work with, let's say that our system handles multiple message types that all refer to a specific client entity we're going
to call "Organization Code." For the sake of performance correlation and troubleshooting later, we would like to have an idea about how the system performance
varies between organizations. To do that, we will be adding the "Organization Code" as a tag to the performance metrics.

First, let's start by using a common interface called `IOrganizationRelated` interface that just provides a common way
of exposing the `OrganizationCode` for these message types handled by Wolverine. Next, the mechanism to adding the "Organization Code" to the metrics is to use the `Envelope.SetMetricsTag()` method
to tag the current message being processed. Going back to the `IOrganizationRelated` marker interface, we can add some middleware that acts on
`IOrganizationRelated` messages to add the metrics tag as shown below:

<!-- snippet: sample_organization_tagging_middleware -->
<a id='snippet-sample_organization_tagging_middleware'></a>
```cs
// Common interface on message types within our system
public interface IOrganizationRelated
{
    string OrganizationCode { get; }
}

// Middleware just to add a metrics tag for the organization code
public static class OrganizationTaggingMiddleware
{
    public static void Before(IOrganizationRelated command, Envelope envelope)
    {
        envelope.SetMetricsTag("org.code", command.OrganizationCode);
    }
}
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Samples/DocumentationSamples/MetricsSamples.cs#L46-L63' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_organization_tagging_middleware' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

Finally, we'll add the new middleware to all message handlers where the message implements the `IOrganizationRelated` interface like so:

<!-- snippet: sample_using_organization_tagging_middleware -->
<a id='snippet-sample_using_organization_tagging_middleware'></a>
```cs
using var host = await Host.CreateDefaultBuilder()
    .UseWolverine(opts =>
    {
        // Add this middleware to all handlers where the message can be cast to
        // IOrganizationRelated
        opts.Policies.ForMessagesOfType<IOrganizationRelated>().AddMiddleware(typeof(OrganizationTaggingMiddleware));
    }).StartAsync();
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Samples/DocumentationSamples/MetricsSamples.cs#L10-L20' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_using_organization_tagging_middleware' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

### Tenant Id Tagging

<!-- snippet: sample_tenant_id_tagging -->
<a id='snippet-sample_tenant_id_tagging'></a>
```cs
public static async Task publish_operation(IMessageBus bus, string tenantId, string name)
{
    // All outgoing messages or executed messages from this 
    // IMessageBus object will be tagged with the tenant id
    bus.TenantId = tenantId;
    await bus.PublishAsync(new SomeMessage(name));
}
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Samples/DocumentationSamples/MetricsSamples.cs#L33-L43' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_tenant_id_tagging' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->
