# Using Google Cloud Platform (GCP) Pub/Sub

::: tip
Wolverine.AzureServiceBus is able to support inline, buffered, or durable endpoints.
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
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Transports/GCP/Wolverine.Pubsub.Tests/DocumentationSamples.cs#L15-L30' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_basic_setup_to_pubsub' title='Start of snippet'>anchor</a></sup>
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
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Transports/GCP/Wolverine.Pubsub.Tests/DocumentationSamples.cs#L35-L48' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_connect_to_pubsub_emulator' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

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
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Transports/GCP/Wolverine.Pubsub.Tests/DocumentationSamples.cs#L53-L62' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_enable_system_endpoints_in_pubsub' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->
