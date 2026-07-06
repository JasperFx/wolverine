# Using Google Cloud Platform (GCP) Pub/Sub

::: tip
Wolverine.Pubsub is able to support inline, buffered, or durable endpoints.
:::

Wolverine supports [GCP Pub/Sub](https://cloud.google.com/pubsub) as a messaging transport through the WolverineFx.Pubsub package.

## Connecting to the Broker

After referencing the Nuget package, the next step to using GCP Pub/Sub within your Wolverine application is to connect to the service broker using the `UsePubsub()` extension method.

If you are running on Google Cloud or with Application Default Credentials (ADC), you just need to supply [your GCP project id](https://support.google.com/googleapi/answer/7014113):

<!-- snippet: sample_basic_setup_to_pubsub -->
<a id='snippet-sample_basic_setup_to_pubsub'></a>
```cs
var host = await Host.CreateDefaultBuilder()
    .UseWolverine(opts =>
    {
        opts.UsePubsub("your-project-id")

            // Let Wolverine create missing topics and subscriptions as necessary
            .AutoProvision()

            // Optionally purge all subscriptions on application startup.
            // Warning though, this is potentially slow
            .AutoPurgeOnStartup();
    }).StartAsync();
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Transports/GCP/Wolverine.Pubsub.Tests/DocumentationSamples.cs#L15-L29' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_basic_setup_to_pubsub' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

If you'd like to connect to a GCP Pub/Sub emulator running on your development box,
you set emulator detection throught this helper:

<!-- snippet: sample_connect_to_pubsub_emulator -->
<a id='snippet-sample_connect_to_pubsub_emulator'></a>
```cs
var host = await Host.CreateDefaultBuilder()
    .UseWolverine(opts =>
    {
        opts.UsePubsub("your-project-id")

            // Tries to use GCP Pub/Sub emulator, as it defaults
            // to EmulatorDetection.EmulatorOrProduction. But you can
            // supply your own, like EmulatorDetection.EmulatorOnly
            .UseEmulatorDetection();
    }).StartAsync();
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Transports/GCP/Wolverine.Pubsub.Tests/DocumentationSamples.cs#L34-L46' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_connect_to_pubsub_emulator' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

### Authentication / Credentials

By default, Wolverine uses [Application Default Credentials](https://cloud.google.com/docs/authentication/application-default-credentials). If you need to supply a specific `GoogleCredential` — for example when running on Azure with Workload Identity Federation — use `UseCredential`:

```csharp
opts.UsePubsub("your-project-id")
    .UseCredential(
        GoogleCredential.FromFile("/path/to/wif-credential-config.json")
    );
```

The credential manages its own token refresh lifecycle, so no additional background task is required. For more control over the underlying GCP client builders, see [Customisation](/guide/messaging/transports/gcp-pubsub/customisation).

## Multiple / Named Brokers

You can connect to more than one GCP Pub/Sub broker (typically a different GCP project) from a single Wolverine application by registering an additional, *named* broker alongside the default one. Endpoints are then pinned to the named broker with the `...OnNamedBroker` overloads:

<!-- snippet: sample_named_pubsub_broker -->
<a id='snippet-sample_named_pubsub_broker'></a>
```cs
var host = await Host.CreateDefaultBuilder()
    .UseWolverine(opts =>
    {
        // The default / shared Pub/Sub broker
        opts.UsePubsub("your-project-id").AutoProvision();

        // An additional, independent Pub/Sub broker pointed at a different GCP project.
        // The Wolverine Uri scheme for endpoints on this broker becomes the broker name
        // ("americas"), e.g. americas://americas-project-id/colors
        opts.AddNamedPubsubBroker(new BrokerName("americas"), "americas-project-id")
            .AutoProvision();

        // Pin specific endpoints to the named broker
        opts.PublishMessage<ColorMessage>()
            .ToPubsubTopicOnNamedBroker(new BrokerName("americas"), "colors");
        opts.ListenToPubsubTopicOnNamedBroker(new BrokerName("americas"), "colors");
    }).StartAsync();
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Transports/GCP/Wolverine.Pubsub.Tests/DocumentationSamples.cs#L67-L84' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_named_pubsub_broker' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

Note that the `Uri` scheme within Wolverine for any endpoint on a *named* GCP Pub/Sub broker is the broker name you supply, not `pubsub`. So in the example above you would see `Uri` values like `americas://americas-project-id/colors`.

## Multi-Tenancy with a Broker Per Tenant

Named brokers (above) are a *static* topology: you pin specific endpoints to a specific broker at configuration time. **Broker-per-tenant** is different — it is *runtime* routing. You declare one shared topic topology, and each tenant is served by its **own dedicated GCP project**. Which project a message goes to (and which project an inbound message came from) is decided at runtime by the message's [tenant id](/guide/handlers/multi-tenancy), typically set through `DeliveryOptions.TenantId`.

Project-id-per-tenant is the natural isolation axis for Pub/Sub: the topic and subscription names embed the project id, so the *same* logical topic under a *different* project is already a physically distinct Pub/Sub resource — "shared by name, isolated by project".

<!-- snippet: sample_pubsub_broker_per_tenant -->
<a id='snippet-sample_pubsub_broker_per_tenant'></a>
```cs
var host = await Host.CreateDefaultBuilder()
    .UseWolverine(opts =>
    {
        // The "default" / shared Pub/Sub connection on its own GCP project
        opts.UsePubsub("shared-project-id")
            .AutoProvision()

            // How should Wolverine route a message whose TenantId is null or
            // unknown? FallbackToDefault (the default) uses the shared project;
            // TenantIdRequired throws; IgnoreUnknownTenants silently drops it.
            .TenantIdBehavior(TenantedIdBehavior.FallbackToDefault)

            // Each tenant is served by its OWN dedicated GCP project, but shares
            // the topic topology declared below. Project-id-per-tenant is the
            // natural isolation axis: the same logical topic under a different
            // project is a physically distinct Pub/Sub resource.
            .AddTenant("tenant1", "tenant1-project-id")

            // A tenant may also carry its own dedicated credentials by configuring
            // its client builders (seeded from the parent transport otherwise):
            .AddTenant("tenant2", "tenant2-project-id", tenant =>
            {
                tenant.ConfigurePublisherApiBuilder =
                    builder => { /* builder.GoogleCredential = ...; */ return ValueTask.CompletedTask; };
            });

        // One shared topology; messages are routed to the right project at runtime
        // by Envelope.TenantId (e.g. new DeliveryOptions { TenantId = "tenant1" }).
        opts.PublishMessage<ColorMessage>().ToPubsubTopic("colors");
        opts.ListenToPubsubTopic("colors");
    }).StartAsync();
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Transports/GCP/Wolverine.Pubsub.Tests/DocumentationSamples.cs#L91-L122' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_pubsub_broker_per_tenant' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

To route a specific message to a tenant's project, stamp the tenant id on the send:

```csharp
await bus.SendAsync(new ColorMessage("blue"), new DeliveryOptions { TenantId = "tenant1" });
```

Wolverine wraps the outbound endpoint in a `TenantedSender` that dispatches on `Envelope.TenantId`, and builds a compound listener that runs one listener per tenant project — each inbound envelope is stamped with the tenant id of the project it was consumed from. When `AutoProvision()` is enabled, Wolverine provisions the shared topology (topics and subscriptions) on **every** tenant project, not just the default one.

::: tip The emulator caveat
The GCP Pub/Sub emulator ignores credentials and accepts arbitrary project ids with no auth, so per-tenant *projects* are trivially testable on a single emulator (just use distinct project id strings). Per-tenant *credentials*, however, cannot be exercised against the emulator — test that path against real GCP.
:::

## Request/Reply

[Request/reply](https://www.enterpriseintegrationpatterns.com/patterns/messaging/RequestReply.html) mechanics (`IMessageBus.InvokeAsync<T>()`) are possible with the GCP Pub/Sub transport *if* Wolverine has the ability to auto-provision a specific response topic and subscription for each node. That topic and subscription would be named like `wlvrn.response.[application node id]` if you happen to notice that in your GCP Pub/Sub.

### Enable System Endpoints

If your application has permissions to create topics and subscriptions in GCP Pub/Sub, you can enable system endpoints and opt in to the request/reply mechanics mentioned above.

<!-- snippet: sample_enable_system_endpoints_in_pubsub -->
<a id='snippet-sample_enable_system_endpoints_in_pubsub'></a>
```cs
var host = await Host.CreateDefaultBuilder()
    .UseWolverine(opts =>
    {
        opts.UsePubsub("your-project-id")
            .EnableSystemEndpoints();
    }).StartAsync();
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Transports/GCP/Wolverine.Pubsub.Tests/DocumentationSamples.cs#L51-L59' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_enable_system_endpoints_in_pubsub' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

## Identifier Prefixing for Shared Environments

When sharing a single GCP project between multiple developers or development environments, you can use `PrefixIdentifiers()` to automatically prepend a prefix to every topic and subscription name created by Wolverine. This helps isolate the cloud resources for each developer or environment:

```csharp
using var host = await Host.CreateDefaultBuilder()
    .UseWolverine(opts =>
    {
        opts.UsePubsub("your-project-id")
            .AutoProvision()

            // Prefix all topic and subscription names with "dev-john."
            .PrefixIdentifiers("dev-john");

        // A topic named "orders" becomes "dev-john.orders"
        opts.ListenToPubsubTopic("orders");
    }).StartAsync();
```

You can also use `PrefixIdentifiersWithMachineName()` as a convenience to use the current machine name as the prefix:

```csharp
opts.UsePubsub("your-project-id")
    .AutoProvision()
    .PrefixIdentifiersWithMachineName();
```

The default delimiter between the prefix and the original name is `.` for GCP Pub/Sub (e.g., `dev-john.orders`).

## URI reference

The `GcpPubsubEndpointUri` helper class builds canonical endpoint URIs:

| URI form | Helper call |
|---|---|
| `pubsub://{projectId}/{topicName}` | `GcpPubsubEndpointUri.Topic("projectId", "topicName")` |

```csharp
using Wolverine.Pubsub;

var uri = GcpPubsubEndpointUri.Topic("my-project", "orders");
```
