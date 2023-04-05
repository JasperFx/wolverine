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

TODO -- Soon. This is going to be tedious

## Metrics

TODO -- talk about extending metrics headers

TODO -- there's quite a bit built in that's published through System.Diagnostics.Metrics that should be available through open telemetry exports,
but some more experimentation and actual docs are forthcoming.


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
