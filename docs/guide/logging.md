# Instrumentation and Metrics

Wolverine logs through the standard .NET `ILogger` abstraction, and there's nothing special you need to do
to enable that logging other than using one of the standard approaches for bootstrapping a .NET application
using `IHostBuilder`. Wolverine is logging all messages sent, received, and executed inline.

::: info
Inside of message handling, Wolverine is using `ILogger<T>` where `T` is the **message type**. So if you want
to selectively filter logging levels in your application, rely on the message type rather than the handler type.
:::

## Configuring Message Logging Levels

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
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Samples/DocumentationSamples/LoggingUsage.cs#L25-L39' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_turning_down_message_logging' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

The sample up above turns down the logging on a global, application level. If you have some kind of command message where
you don't want logging for that particular message type, but do for all other message types, you can override the log
level for only that specific message type like so:

<!-- snippet: sample_customized_handler_using_configure -->
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
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Testing/CoreTests/Configuration/can_customize_handler_chain_through_Configure_call_on_HandlerType.cs#L25-L45' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_customized_handler_using_configure' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

Methods on message handler types with the signature:

```csharp
public static void Configure(HandlerChain chain)
```

will be called by Wolverine to apply message type specific overrides to Wolverine's message handling.

## Full Tracing for InvokeAsync <Badge type="tip" text="5.25" />

By default, messages processed via `InvokeAsync()` (Wolverine's in-process mediator) use lightweight tracking
without emitting the same structured log messages that transport-received messages produce. If you need full
observability for inline invocations — for example, when using Wolverine purely as a mediator within an HTTP
application — you can opt into full tracing:

```csharp
using var host = await Host.CreateDefaultBuilder()
    .UseWolverine(opts =>
    {
        // Emit the same structured log messages for InvokeAsync()
        // as Wolverine does for transport-received messages
        opts.InvokeTracing = InvokeTracingMode.Full;
    }).StartAsync();
```

When `InvokeTracingMode.Full` is enabled, `InvokeAsync()` will emit:
- **Execution started** — logged at the configured `MessageExecutionLogLevel` (default `Debug`)
- **Message succeeded** — logged at the configured `MessageSuccessLogLevel` (default `Information`)
- **Message failed** — logged at `Error` level with the exception
- **Execution finished** — logged at the configured `MessageExecutionLogLevel`

These are the same log messages and event IDs that Wolverine already uses for messages received from
external transports like RabbitMQ, Kafka, or Azure Service Bus. This makes it easy to use a single
log query to observe all message processing regardless of how messages enter the system.

## Configuring Health Check Tracing

Wolverine's node agent controller performs health checks periodically (every 10 seconds by default) to maintain node assignments and cluster state. By default, these health checks emit Open Telemetry traces named `wolverine_node_assignments`, which can result in high trace volumes in observability platforms.

You can control this tracing behavior through the `DurabilitySettings`:

<!-- snippet: sample_configuring_health_check_tracing -->
<a id='snippet-sample_configuring_health_check_tracing'></a>
```cs
// Disable the "wolverine_node_assignments" traces entirely
opts.Durability.NodeAssignmentHealthCheckTracingEnabled = false;

// Or, sample those traces to only once every 10 minutes
// opts.Durability.NodeAssignmentHealthCheckTraceSamplingPeriod = TimeSpan.FromMinutes(10);
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Testing/OpenTelemetry/OtelWebApiWolverineMarten/Program.cs#L18-L24' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_configuring_health_check_tracing' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

## Controlling Message Specific Logging and Tracing

While Open Telemetry tracing can be disabled on an endpoint by endpoint basis, you may want to disable Open Telemetry
tracing for specific message types. You may also want to modify the log levels for message success and message execution
on a message type by message type basis. While you *can* also do that with custom handler chain policies, the easiest
way to do that is to use the `[WolverineLogging]` attribute on either the handler type or the handler method as shown 
below:

<!-- snippet: sample_using_wolverine_logging_attribute -->
<a id='snippet-sample_using_wolverine_logging_attribute'></a>
```cs
public record QuietMessage;

public record VerboseMessage;

public class QuietAndVerboseMessageHandler
{
    [WolverineLogging(
        telemetryEnabled:false,
        successLogLevel: LogLevel.None,
        executionLogLevel:LogLevel.Trace)]
    public void Handle(QuietMessage message)
    {
        Console.WriteLine("Hush!");
    }
    
    [WolverineLogging(
        // Enable Open Telemetry tracing
        TelemetryEnabled = true, 
        
        // Log on successful completion of this message
        SuccessLogLevel = LogLevel.Information, 
        
        // Log on execution being complete, but before Wolverine does its own book keeping
        ExecutionLogLevel = LogLevel.Information, 
        
        // Throw in yet another contextual logging statement
        // at the beginning of message execution
        MessageStartingLevel = LogLevel.Debug)]
    public void Handle(VerboseMessage message)
    {
        Console.WriteLine("Tell me about it!");
    }
}
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Testing/CoreTests/Acceptance/logging_configuration.cs#L78-L113' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_using_wolverine_logging_attribute' title='Start of snippet'>anchor</a></sup>
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
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Samples/DocumentationSamples/LoggingUsage.cs#L11-L20' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_log_message_starting' title='Start of snippet'>anchor</a></sup>
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

::: tip
As of verion 5.5, Wolverine will automatically audit any property that refers to a [saga identity](/guide/durability/sagas) or to an event stream
identity within the [aggregate handler workflow](/guide/durability/marten/event-sourcing) with Marten event sourcing.
:::

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
    public string Name { get; set; } = null!;

    [Audit("AccountIdentifier")] public int AccountId;
}
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Testing/CoreTests/Configuration/auditing_determination.cs#L101-L110' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_using_audit_attribute' title='Start of snippet'>anchor</a></sup>
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
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Testing/CoreTests/Configuration/auditing_determination.cs#L135-L145' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_account_message_for_auditing' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

You can specify audited members through this syntax:

<!-- snippet: sample_explicit_registration_of_audit_properties -->
<a id='snippet-sample_explicit_registration_of_audit_properties'></a>
```cs
// opts is WolverineOptions inside of a UseWolverine() call
opts.Policies.ForMessagesOfType<IAccountMessage>().Audit(x => x.AccountId);
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Testing/CoreTests/Configuration/auditing_determination.cs#L73-L77' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_explicit_registration_of_audit_properties' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

This will extend your log entries to like this:

```text
[09:41:00 INFO] Starting to process IAccountMessage ("018761ad-8ed2-4bc9-bde5-c3cbb643f9f3") with AccountId: "c446fa0b-7496-42a5-b6c8-dd53c65c96c8"
```

## Wire Tap <Badge type="tip" text="5.13" />

Wolverine supports the [Wire Tap](https://www.enterpriseintegrationpatterns.com/patterns/messaging/WireTap.html) pattern
from the Enterprise Integration Patterns book. A wire tap lets you record a copy of every message flowing through
configured endpoints for auditing, compliance, analytics, or monitoring purposes — without affecting the primary
message processing pipeline.

### Defining a Wire Tap

Implement the `IWireTap` interface:

```csharp
public class AuditWireTap : IWireTap
{
    private readonly IAuditStore _store;

    public AuditWireTap(IAuditStore store)
    {
        _store = store;
    }

    public async ValueTask RecordSuccessAsync(Envelope envelope)
    {
        await _store.RecordAsync(new AuditEntry
        {
            MessageId = envelope.Id,
            MessageType = envelope.MessageType,
            Destination = envelope.Destination?.ToString(),
            Timestamp = DateTimeOffset.UtcNow,
            Succeeded = true
        });
    }

    public async ValueTask RecordFailureAsync(Envelope envelope, Exception exception)
    {
        await _store.RecordAsync(new AuditEntry
        {
            MessageId = envelope.Id,
            MessageType = envelope.MessageType,
            Destination = envelope.Destination?.ToString(),
            Timestamp = DateTimeOffset.UtcNow,
            Succeeded = false,
            ExceptionType = exception.GetType().Name,
            ExceptionMessage = exception.Message
        });
    }
}
```

::: warning
**Implementations must never allow exceptions to escape.** Wolverine wraps wire tap calls in a safety-net `try/catch`,
but if your wire tap throws, the exception will only be logged — it will *not* retry or affect message processing.
Your implementation should handle all errors internally (e.g., log and swallow) to avoid polluting application logs
with wire tap noise.
:::

::: tip
For production wire taps that write to a database or external system, consider using `System.Threading.Channels`
(specifically Wolverine's built-in `BatchingChannel`) to batch the recording operations. This keeps the wire tap
mechanics off the hot path of message handling, improving throughput while batching database writes for efficiency.
:::

### Registering a Wire Tap

Register your `IWireTap` in the IoC container. **Singleton lifetime is strongly recommended** since wire taps are
resolved once per endpoint at startup:

```csharp
using var host = await Host.CreateDefaultBuilder()
    .UseWolverine(opts =>
    {
        // Register a singleton wire tap
        opts.Services.AddSingleton<IWireTap, AuditWireTap>();
    }).StartAsync();
```

### Enabling Wire Taps on Endpoints

Wire taps must be explicitly enabled on each endpoint — there is no global "enable everywhere" switch. This is
intentional: you should deliberately choose which endpoints need auditing.

```csharp
using var host = await Host.CreateDefaultBuilder()
    .UseWolverine(opts =>
    {
        opts.Services.AddSingleton<IWireTap, AuditWireTap>();

        // Enable on a specific listener
        opts.ListenToRabbitQueue("incoming").UseWireTap();

        // Enable on a specific sender
        opts.PublishAllMessages().ToRabbitExchange("outgoing").UseWireTap();

        // Enable on a specific local queue
        opts.LocalQueue("important").UseWireTap();

        // Enable across all external listeners (excludes local queues)
        opts.Policies.AllListeners(x => x.UseWireTap());

        // Enable across all local queues separately
        opts.Policies.AllLocalQueues(x => x.UseWireTap());

        // Enable across all sender endpoints
        opts.Policies.AllSenders(x => x.UseWireTap());
    }).StartAsync();
```

### Using Keyed Wire Taps

If different endpoints need different wire tap implementations (e.g., one endpoint writes to a compliance database
while another sends to a monitoring service), use keyed services:

```csharp
using var host = await Host.CreateDefaultBuilder()
    .UseWolverine(opts =>
    {
        // Register multiple wire tap implementations
        opts.Services.AddSingleton<IWireTap, ComplianceWireTap>();
        opts.Services.AddKeyedSingleton<IWireTap>("monitoring", new MonitoringWireTap());

        // Default wire tap (uses the non-keyed registration)
        opts.ListenToRabbitQueue("orders").UseWireTap();

        // Specific wire tap by service key
        opts.ListenToRabbitQueue("payments").UseWireTap("monitoring");
    }).StartAsync();
```

### What Gets Recorded

- **`RecordSuccessAsync`** is called when:
  - A message has been successfully handled at a listening endpoint
  - A message has been successfully sent from a sending endpoint
- **`RecordFailureAsync`** is called when:
  - Message handling fails at a listening endpoint after exhausting all error handling policies (moved to dead letter queue)

### Auditing and Compliance Considerations

For systems with regulatory auditing requirements (SOC 2, HIPAA, PCI-DSS, GDPR):

- Wire taps provide a natural integration point for recording message flow for audit trails
- Combine with Wolverine's [contextual logging and audited members](#contextual-logging-with-audited-members) to include business identifiers in your audit records
- The `Envelope` passed to wire tap methods includes correlation IDs, tenant IDs, and message metadata useful for compliance reporting
- Consider separate wire tap implementations per compliance domain using keyed services

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
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Testing/OpenTelemetry/OtelWebApi/Program.cs#L36-L51' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_enabling_open_telemetry' title='Start of snippet'>anchor</a></sup>
<a id='snippet-sample_enabling_open_telemetry-1'></a>
```cs
builder.Services.AddOpenTelemetry()
    .WithTracing(tracing => { tracing.AddSource("Wolverine"); })
    .WithMetrics(metrics => { metrics.AddMeter("Wolverine"); })
    .UseOtlpExporter();
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Testing/OpenTelemetry/OtelWebApiWolverineMarten/Program.cs#L36-L42' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_enabling_open_telemetry-1' title='Start of snippet'>anchor</a></sup>
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
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Samples/DocumentationSamples/DisablingOpenTelemetry.cs#L11-L25' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_disabling_open_telemetry_by_endpoint' title='Start of snippet'>anchor</a></sup>
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

/// <summary>
/// Activity tag for the saga identity value when processing a saga message
/// </summary>
public const string SagaId = "wolverine.saga.id";

/// <summary>
/// Activity tag for the saga type full name when processing a saga message
/// </summary>
public const string SagaType = "wolverine.saga.type";

/// <summary>
/// Activity tag for the aggregate stream identity when processing an aggregate handler workflow
/// </summary>
public const string StreamId = "wolverine.stream.id";

/// <summary>
/// Activity tag for the aggregate type full name when processing an aggregate handler workflow
/// </summary>
public const string StreamType = "wolverine.stream.type";
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Wolverine/Runtime/WolverineTracing.cs#L28-L141' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_wolverine_open_telemetry_tracing_spans_and_activities' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

## Opt-in Handler Execution Diagnostics <Badge type="tip" text="5.38" />

The default span surface above covers cluster-level events (listener pause, retries, scheduled redelivery, etc.). Wolverine ships a separate, **opt-in** layer of structured diagnostics aimed primarily at **performance optimization** — handler latency tuning, spotting queue dwell or backpressure, profiling slow transactional commits, and debugging `await`-graph interleaving inside the handler. Each flag lives on `WolverineOptions.Tracking` and **defaults to `false`**, so apps that don't ask for the surface pay nothing for it.

```csharp
builder.UseWolverine(opts =>
{
    // wolverine.handler.started / wolverine.handler.finished ActivityEvents
    // around the user handler body, plus per-envelope timing tags.
    opts.Tracking.HandlerExecutionDiagnosticsEnabled = true;

    // wolverine.deserialize span around inbound envelope deserialization,
    // tagged with messaging.message_payload_size_bytes.
    opts.Tracking.DeserializationSpanEnabled = true;

    // wolverine.outbox.flushing / wolverine.outbox.published ActivityEvents
    // around the FlushOutgoingMessages call in the generated handler chain,
    // and (when Wolverine.Marten transactional middleware is in play)
    // marten.savechanges.start / marten.savechanges.finished ActivityEvents
    // around the Marten IDocumentSession.SaveChangesAsync call.
    opts.Tracking.OutboxDiagnosticsEnabled = true;

    // RecordCauseAndEffect call after the handler body that reports unique
    // (incoming, outgoing) message-type pairs to IWolverineObserver.
    opts.Tracking.EnableMessageCausationTracking = true;
});
```

Each flag is independent. The runtime checks each flag at code-generation time only — when a flag is `false`, the corresponding annotations are not emitted into the generated handler at all, so there is **zero per-message runtime cost** for any feature you haven't enabled. That codegen-time gating is the whole point: in tight production hot paths you want a flag that costs literally nothing when it's off, not one guarded by a runtime `if`. The legacy `WolverineOptions.EnableMessageCausationTracking` property is preserved as an `[Obsolete]` shim that delegates to `Tracking.EnableMessageCausationTracking`.

### `HandlerExecutionDiagnosticsEnabled`

When set, two ActivityEvents and two activity tags are emitted around each handler invocation:

| Name | Kind | Meaning |
|---|---|---|
| `wolverine.handler.started` | ActivityEvent | Emitted immediately before the user handler body runs, after every middleware frame has completed. Lets you measure middleware overhead independently. |
| `wolverine.handler.finished` | ActivityEvent | Emitted immediately after the user handler body returns successfully. |
| `wolverine.envelope.transport_lag_ms` | tag (double, milliseconds) | `activity.StartTimeUtc - envelope.SentAt` — the elapsed time from when the producer stamped the envelope's send timestamp to when the consumer's handler activity started. Skipped for negative values (clock drift). |
| `wolverine.envelope.receive_dwell_ms` | tag (double, milliseconds) | `activity.StartTimeUtc - envelope.ReceivedAt` — the elapsed time from when the listener stamped the envelope as received (`Envelope.MarkReceived`) to when the handler activity started. Useful for spotting in-process worker-queue backpressure separately from upstream transport latency. Absent for envelopes that didn't traverse a receiver (inline `IMessageBus.InvokeAsync` calls). |

The two ActivityEvents are emitted by the JasperFx `MethodCall.ActivityEventBeforeCall` / `.ActivityEventAfterCall` codegen surface, so they wrap exactly the user handler `MethodCall` — middleware frames before the handler body stay unmarked. The two timing tags are stamped by an `ApplyExecutionDiagnosticTagsFrame` that's prepended to the generated chain when the flag is set, so all the tag computation happens inline in the generated handler with no runtime branching in `Executor` or `HandlerPipeline`.

### `DeserializationSpanEnabled`

When set, Wolverine starts a `wolverine.deserialize` span (kind = `Internal`) around the inbound envelope deserialization that runs before the handler chain executes. The span carries:

| Tag | Meaning |
|---|---|
| `messaging.message_payload_size_bytes` | The size of the raw envelope `Data` array in bytes. |

The span's status is set to `Error` (with the exception type name as description) when deserialization throws — useful for separating "transport delivered me garbage" from "my handler blew up" in trace dashboards. The span only starts when the flag is on, so apps that don't enable it see no extra spans.

### `OutboxDiagnosticsEnabled`

When set, Wolverine emits two ActivityEvents around the post-handler call to `IMessageContext.FlushOutgoingMessagesAsync` in the generated handler chain:

| Name | Meaning |
|---|---|
| `wolverine.outbox.flushing` | Emitted immediately before the outbox flush call. |
| `wolverine.outbox.published` | Emitted immediately after the outbox flush call returns successfully. |

This is provider-agnostic — it fires regardless of which transactional middleware (Marten, EF Core, RDBMS, Polecat, etc.) added the `FlushOutgoingMessages` postprocessor frame. The annotation is emitted via the same JasperFx `MethodCall.ActivityEventBeforeCall` / `.ActivityEventAfterCall` codegen surface as the handler events, so when the flag is off the generated outbox-flush call has no extra emission.

When the chain pulls in **Wolverine.Marten** transactional middleware, `OutboxDiagnosticsEnabled` also brackets the Marten transactional commit:

| Name | Meaning |
|---|---|
| `marten.savechanges.start` | Emitted immediately before `IDocumentSession.SaveChangesAsync(CancellationToken)`. |
| `marten.savechanges.finished` | Emitted immediately after `SaveChangesAsync` returns successfully. |

Useful for separating "the database commit is slow" from "the broker publish is slow" when profiling a transactional handler — the two pairs of events bracket the two distinct stages.

### `EnableMessageCausationTracking`

When set, Wolverine emits a `RecordCauseAndEffect(context, context.Runtime.Observer)` call into the generated handler **between** the handler body and the postprocessor frames. Each unique `(incoming → outgoing, handler)` triple is reported once to `IWolverineObserver.MessageCausedBy` for downstream topology visualization (CritterWatch enables this flag automatically). Latched on the framework side, so the call itself is cheap on the steady-state path; the codegen-time gate guarantees zero cost for users who don't enable it.

### `Envelope.ReceivedAt`

The `wolverine.envelope.receive_dwell_ms` tag depends on a new `Envelope.ReceivedAt` property that's stamped by `Envelope.MarkReceived` — the single point all receivers (`BufferedReceiver`, `DurableReceiver`, etc.) call when a message is handed off from a listener to the worker pipeline. The property is `[JsonIgnore]` and only set by Wolverine itself; it stays `null` for envelopes that didn't traverse a receiver (inline `InvokeAsync` calls).

### Sample of the generated handler code

The "zero per-message runtime cost when off" property is easiest to see by inspecting the C# Wolverine actually generates. Take a trivial handler:

```csharp
public record TrackingDiagnosticsMessage(string Text);

public static class TrackingDiagnosticsHandler
{
    public static void Handle(TrackingDiagnosticsMessage message) { /* no-op */ }
}
```

With **all flags off** (the default), the generated `HandleAsync` body is the bare handler invocation — no diagnostic plumbing is emitted at all:

```csharp
public override Task HandleAsync(MessageContext context, CancellationToken cancellation)
{
    var trackingDiagnosticsMessage = (TrackingDiagnosticsMessage)context.Envelope.Message;

    Activity.Current?.SetTag("message.handler", "TrackingDiagnosticsHandler");
    Activity.Current?.SetTag("handler.type",    "TrackingDiagnosticsHandler");

    TrackingDiagnosticsHandler.Handle(trackingDiagnosticsMessage);

    return Task.CompletedTask;
}
```

With `Tracking.HandlerExecutionDiagnosticsEnabled = true` and `Tracking.EnableMessageCausationTracking = true`, the generated body picks up an `ApplyExecutionDiagnosticTags` call at the top, ActivityEvents bracketing the handler body, and a `RecordCauseAndEffect` call between the handler and the postprocessor frames:

```csharp
public override Task HandleAsync(MessageContext context, CancellationToken cancellation)
{
    var trackingDiagnosticsMessage = (TrackingDiagnosticsMessage)context.Envelope.Message;

    WolverineTracing.ApplyExecutionDiagnosticTags(Activity.Current, context.Envelope);
    Activity.Current?.SetTag("message.handler", "TrackingDiagnosticsHandler");
    Activity.Current?.SetTag("handler.type",    "TrackingDiagnosticsHandler");

    Activity.Current?.AddEvent(new ActivityEvent("wolverine.handler.started"));
    TrackingDiagnosticsHandler.Handle(trackingDiagnosticsMessage);
    Activity.Current?.AddEvent(new ActivityEvent("wolverine.handler.finished"));

    RecordCauseAndEffect(context, context.Runtime.Observer);
    return Task.CompletedTask;
}
```

Compare the two and the design pattern is concrete: each opt-in flag adds a specific line to the generated method; turning it back off removes the line entirely. There's no runtime `if (options.Tracking.X)` check anywhere in the framework hot path — the chain's `assembleFrames` reads each flag once at codegen time and decides which frames to emit.

If you want to inspect what your own handlers look like, set `WolverineOptions.CodeGeneration.SourceCodeWritingEnabled = true` and dump the generated code (or call `host.Services.GetRequiredService<HandlerGraph>().ChainFor<MyMessage>()!.SourceCode`); the same `tracking_diagnostics_opt_in` test suite in the Wolverine repo writes the generated source to xUnit output for every flag combination, so the contract is regression-tested rather than just illustrated here.

## Handler Type Tagging

Wolverine automatically tags Open Telemetry activity spans with the handler type name during message processing. This provides per-handler tracing visibility in observability backends like Jaeger, Zipkin, or Honeycomb without any additional configuration.

For both message handlers and Wolverine.HTTP endpoints, Wolverine emits the `handler.type` tag containing the full .NET type name of the handler class. For message handlers, the existing `message.handler` tag is also set with the same value for backward compatibility.

These tags are memoized as string literals in Wolverine's generated code, so there is no runtime cost for computing the handler type name on each request.

Example activity tags for a message handler:
```
handler.type = "MyApp.Handlers.OrderPlacedHandler"
message.handler = "MyApp.Handlers.OrderPlacedHandler"
```

Example activity tags for an HTTP endpoint:
```
handler.type = "MyApp.Endpoints.OrderEndpoint"
```

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
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Samples/DocumentationSamples/MetricsSamples.cs#L41-L57' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_organization_tagging_middleware' title='Start of snippet'>anchor</a></sup>
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
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Samples/DocumentationSamples/MetricsSamples.cs#L10-L19' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_using_organization_tagging_middleware' title='Start of snippet'>anchor</a></sup>
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
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Samples/DocumentationSamples/MetricsSamples.cs#L29-L38' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_tenant_id_tagging' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->
