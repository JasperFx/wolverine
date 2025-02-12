# Instrumentation and Metrics

Wolverine logs through the standard .NET `ILogger` abstraction, and there's nothing special you need to do
to enable that logging other than using one of the standard approaches for bootstrapping a .NET application
using `IHostBuilder`. Wolverine is logging all messages sent, received, and executed inline.

::: info
Inside of message handling, Wolverine is using `ILogger<T>` where `T` is the **message type**. So if you want
to selectively filter logging levels in your application, rely on the message type rather than the handler type.
:::

## Configuring Message Logging Levels

::: tip
This functionality was added in Wolverine 1.7.
:::

Wolverine automatically logs the execution start and stop of all message handling with `LogLevel.Debug`. Likewise, Wolverine
logs the successful completion of all messages (including the capture of cascading messages and all middleware) with `LogLevel.Information`.
However, many folks have found this logging to be too intrusive. Not to worry, you can quickly override the log levels
within Wolverine for your system like so:

<!-- snippet: sample_turning_down_message_logging -->
<a id='snippet-sample_turning_down_message_logging'></a>
```cs
using var host = await Host.CreateDefaultBuilder()
    .UseWolverine(opts =>
    {
        // Turn off all logging of the message execution starting and finishing
        // The default is Debug
        opts.Policies.MessageExecutionLogLevel(LogLevel.None);

        // Turn down Wolverine's built in logging of all successful
        // message processing
        opts.Policies.MessageSuccessLogLevel(LogLevel.Debug);
    }).StartAsync();
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Samples/DocumentationSamples/LoggingUsage.cs#L26-L41' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_turning_down_message_logging' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

The sample up above turns down the logging on a global, application level. If you have some kind of command message where
you don't want logging for that particular message type, but do for all other message types, you can override the log
level for only that specific message type like so:

<!-- snippet: sample_customized_handler_using_Configure -->
<a id='snippet-sample_customized_handler_using_configure'></a>
```cs
public class CustomizedHandler
{
    public void Handle(SpecialMessage message)
    {
        // actually handle the SpecialMessage
    }

    public static void Configure(HandlerChain chain)
    {
        chain.Middleware.Add(new CustomFrame());

        // Turning off all execution tracking logging
        // from Wolverine for just this message type
        // Error logging will still be enabled on failures
        chain.SuccessLogLevel = LogLevel.None;
        chain.ProcessingLogLevel = LogLevel.None;
    }
}
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Testing/CoreTests/Configuration/can_customize_handler_chain_through_Configure_call_on_HandlerType.cs#L25-L46' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_customized_handler_using_configure' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

Methods on message handler types with the signature:

```csharp
public static void Configure(HandlerChain chain)
```

will be called by Wolverine to apply message type specific overrides to Wolverine's message handling.

## Controlling Message Specific Logging and Tracing

While Open Telemetry tracing can be disabled on an endpoint by endpoint basis, you may want to disable Open Telemetry
tracing for specific message types. You may also want to modify the log levels for message success and message execution
on a message type by message type basis. While you *can* also do that with custom handler chain policies, the easiest
way to do that is to use the `[WolverineLogging]` attribute on either the handler type or the handler method as shown 
below:

<!-- snippet: sample_using_Wolverine_Logging_attribute -->
<a id='snippet-sample_using_wolverine_logging_attribute'></a>
```cs
public class QuietMessage;

public class QuietMessageHandler
{
    [WolverineLogging(
        telemetryEnabled:false,
        successLogLevel: LogLevel.None,
        executionLogLevel:LogLevel.Trace)]
    public void Handle(QuietMessage message)
    {
        Console.WriteLine("Hush!");
    }
}
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Testing/CoreTests/Acceptance/logging_configuration.cs#L27-L43' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_using_wolverine_logging_attribute' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->


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

This will append log entries looking like this:

```text
[09:41:00 INF] Starting to process <MessageType> (<MessageId>)
```

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
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Testing/CoreTests/Configuration/auditing_determination.cs#L86-L96' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_using_audit_attribute' title='Start of snippet'>anchor</a></sup>
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
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Testing/CoreTests/Configuration/auditing_determination.cs#L111-L122' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_account_message_for_auditing' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

You can specify audited members through this syntax:

<!-- snippet: sample_explicit_registration_of_audit_properties -->
<a id='snippet-sample_explicit_registration_of_audit_properties'></a>
```cs
// opts is WolverineOptions inside of a UseWolverine() call
opts.Policies.ForMessagesOfType<IAccountMessage>().Audit(x => x.AccountId);
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Testing/CoreTests/Configuration/auditing_determination.cs#L73-L78' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_explicit_registration_of_audit_properties' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

This will extend your log entries to like this:

```text
[09:41:00 INFO] Starting to process IAccountMessage ("018761ad-8ed2-4bc9-bde5-c3cbb643f9f3") with AccountId: "c446fa0b-7496-42a5-b6c8-dd53c65c96c8"
```

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

::: tip
Wolverine 1.7 added the ability to disable Open Telemetry tracing on an endpoint by endpoint basis, and **finally** turned
off Otel tracing of internal Wolverine messages
:::

Open Telemetry tracing can be selectively disabled on an endpoint by endpoint basis with this API:

<!-- snippet: sample_disabling_open_telemetry_by_endpoint -->
<a id='snippet-sample_disabling_open_telemetry_by_endpoint'></a>
```cs
using var host = await Host.CreateDefaultBuilder()
    .UseWolverine(opts =>
    {
        opts
            .PublishAllMessages()
            .ToPort(2222)

            // Disable Open Telemetry data collection on
            // all messages sent, received, or executed
            // from this endpoint
            .TelemetryEnabled(false);
    }).StartAsync();
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Samples/DocumentationSamples/DisablingOpenTelemetry.cs#L11-L26' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_disabling_open_telemetry_by_endpoint' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

Note that this `TelemetryEnabled()` method is available on all possible subscriber and listener types within Wolverine.
This flag applies to all messages sent, received, or executed at a particular endpoint.

Wolverine endeavors to publish OpenTelemetry spans or activities for meaningful actions within a Wolverine application. Here
are the specific span names, activity names, and tag names emitted by Wolverine:

<!-- snippet: sample_wolverine_open_telemetry_tracing_spans_and_activities -->
<a id='snippet-sample_wolverine_open_telemetry_tracing_spans_and_activities'></a>
```cs
/// <summary>
/// ActivityEvent marking when an incoming envelope is discarded
/// </summary>
public const string EnvelopeDiscarded = "wolverine.envelope.discarded";

/// <summary>
/// ActivityEvent marking when an incoming envelope is being moved to the error queue
/// </summary>
public const string MovedToErrorQueue = "wolverine.error.queued";

/// <summary>
/// ActivityEvent marking when an incoming envelope does not have a known message
/// handler and is being shunted to registered "NoHandler" actions
/// </summary>
public const string NoHandler = "wolverine.no.handler";

/// <summary>
/// ActivityEvent marking when a message failure is configured to pause the message listener
/// where the message was handled. This is tied to error handling policies
/// </summary>
public const string PausedListener = "wolverine.paused.listener";

/// <summary>
/// Span that is emitted when a listener circuit breaker determines that there are too many
/// failures and listening should be paused
/// </summary>
public const string CircuitBreakerTripped = "wolverine.circuit.breaker.triggered";

/// <summary>
/// Span emitted when a listening agent is started or restarted
/// </summary>
public const string StartingListener = "wolverine.starting.listener";

/// <summary>
/// Span emitted when a listening agent is stopping
/// </summary>
public const string StoppingListener = "wolverine.stopping.listener";

/// <summary>
/// Span emitted when a listening agent is being paused
/// </summary>
public const string PausingListener = "wolverine.pausing.listener";

/// <summary>
/// ActivityEvent marking that an incoming envelope is being requeued after a message
/// processing failure
/// </summary>
public const string EnvelopeRequeued = "wolverine.envelope.requeued";

/// <summary>
/// ActivityEvent marking that an incoming envelope is being retried after a message
/// processing failure
/// </summary>
public const string EnvelopeRetry = "wolverine.envelope.retried";

/// <summary>
/// ActivityEvent marking than an incoming envelope has been rescheduled for later
/// execution after a failure
/// </summary>
public const string ScheduledRetry = "wolverine.envelope.rescheduled";

/// <summary>
/// Tag name trying to explain why a sender or listener was stopped or paused
/// </summary>
public const string StopReason = "wolverine.stop.reason";

/// <summary>
/// The Wolverine Uri that identifies what sending or listening endpoint the activity
/// refers to
/// </summary>
public const string EndpointAddress = "wolverine.endpoint.address";

/// <summary>
/// A stop reason when back pressure policies call for a pause in processing in a single endpoint
/// </summary>
public const string TooBusy = "TooBusy";

/// <summary>
/// A span emitted when a sending agent for a specific endpoint is paused
/// </summary>
public const string SendingPaused = "wolverine.sending.pausing";

/// <summary>
/// A span emitted when a sending agent is resuming after having been paused
/// </summary>
public const string SendingResumed = "wolverine.sending.resumed";

/// <summary>
/// A stop reason when sending agents are paused after too many sender failures
/// </summary>
public const string TooManySenderFailures = "TooManySenderFailures";
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Wolverine/Runtime/WolverineTracing.cs#L27-L121' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_wolverine_open_telemetry_tracing_spans_and_activities' title='Start of snippet'>anchor</a></sup>
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

::: warning
The metrics for the inbox, outbox, and scheduled message counts were unfortunately lost when Wolverine introduced multi-tenancy. They
will be added back to Wolverine in 4.0.
:::

| Metric Name                  | Metric Type                                                                                               | Description                                                                                                                                                                                                                                                                            |
|------------------------------|-----------------------------------------------------------------------------------------------------------|----------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------|
| wolverine-messages-sent      | [Counter](https://opentelemetry.io/docs/reference/specification/metrics/api/#counter)                     | Number of messages sent                                                                                                                                                                                                                                                                |
| wolverine-execution-time     | [Histogram](https://opentelemetry.io/docs/reference/specification/metrics/api/#histogram)                 | Execution time in milliseconds                                                                                                                                                                                                                                                         |
| wolverine-messages-succeeded | Counter                                                                                                   | Number of messages successfully processed                                                                                                                                                                                                                                              |
| wolverine-dead-letter-queue  | Counter                                                                                                   | Number of messages moved to dead letter queues                                                                                                                                                                                                                                         |
| wolverine-effective-time     | Histogram                                                                                                 | Effective time between a message being sent and being completely handled in milliseconds. Right now this works between Wolverine to Wolverine application sending and from NServiceBus applications sending to Wolverine applications through Wolverine’s NServiceBus interoperability. |
| wolverine-execution-failure  | Counter                                                                                                   | Number of message execution failures. Tagged by exception type                                                                                                                                                                                                                         |

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
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Samples/DocumentationSamples/MetricsSamples.cs#L43-L60' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_organization_tagging_middleware' title='Start of snippet'>anchor</a></sup>
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
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Samples/DocumentationSamples/MetricsSamples.cs#L30-L40' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_tenant_id_tagging' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->
