# Using Azure Service Bus

::: tip
Wolverine.AzureServiceBus is able to support inline, buffered, or durable endpoints.
:::

Wolverine supports [Azure Service Bus](https://learn.microsoft.com/en-us/azure/service-bus-messaging/service-bus-messaging-overview) as a messaging transport through the WolverineFx.AzureServiceBus nuget.

## Connecting to the Broker

After referencing the Nuget package, the next step to using Azure Service Bus within your Wolverine
application is to connect to the service broker using the `UseAzureServiceBus()` extension
method as shown below in this basic usage:

<!-- snippet: sample_basic_connection_to_azure_service_bus -->
<a id='snippet-sample_basic_connection_to_azure_service_bus'></a>
```cs
var builder = Host.CreateApplicationBuilder();
builder.UseWolverine(opts =>
{
    // One way or another, you're probably pulling the Azure Service Bus
    // connection string out of configuration
    var azureServiceBusConnectionString = builder
        .Configuration
        .GetConnectionString("azure-service-bus");

    // Connect to the broker in the simplest possible way
    opts.UseAzureServiceBus(azureServiceBusConnectionString)

        // Let Wolverine try to initialize any missing queues
        // on the first usage at runtime
        .AutoProvision()

        // Direct Wolverine to purge all queues on application startup.
        // This is probably only helpful for testing
        .AutoPurgeOnStartup();

    // Or if you need some further specification...
    opts.UseAzureServiceBus(azureServiceBusConnectionString,
        azure => { azure.RetryOptions.Mode = ServiceBusRetryMode.Exponential; });
});

using var host = builder.Build();
await host.StartAsync();
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Transports/Azure/Wolverine.AzureServiceBus.Tests/DocumentationSamples.cs#L14-L44' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_basic_connection_to_azure_service_bus' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

The advanced configuration for the broker is the [ServiceBusClientOptions](https://learn.microsoft.com/en-us/dotnet/api/azure.messaging.servicebus.servicebusclientoptions?view=azure-dotnet) class from the Azure.Messaging.ServiceBus
library. 

For security purposes, there are overloads of `UseAzureServiceBus()` that will also accept and opt into Azure Service Bus authentication with:

1. [TokenCredential](https://learn.microsoft.com/en-us/dotnet/api/azure.core.tokencredential?view=azure-dotnet)
2. [AzureNamedKeyCredential](https://learn.microsoft.com/en-us/dotnet/api/azure.azurenamedkeycredential?view=azure-dotnet)
3. [AzureSasCredential](https://learn.microsoft.com/en-us/dotnet/api/azure.azuresascredential?view=azure-dotnet)

## Request/Reply

[Request/reply](https://www.enterpriseintegrationpatterns.com/patterns/messaging/RequestReply.html) mechanics (`IMessageBus.InvokeAsync<T>()`) are possible with the Azure Service Bus transport *if* Wolverine has the ability to auto-provision
a specific response queue for each node. That queue would be named like `wolverine.response.[application node id]` if you happen
to notice that in the Azure Portal.

And also see the next section. 

## Disabling System Queues

If your application will not have permissions to create temporary queues in Azure Service Bus, you will probably want
to disable system queues to avoid having some annoying error messages popping up. That's easy enough though:

<!-- snippet: sample_disable_system_queues_in_azure_service_bus -->
<a id='snippet-sample_disable_system_queues_in_azure_service_bus'></a>
```cs
var host = await Host.CreateDefaultBuilder()
    .UseWolverine(opts =>
    {
        opts.UseAzureServiceBusTesting()
            .AutoProvision().AutoPurgeOnStartup()
            .SystemQueuesAreEnabled(false);

        opts.ListenToAzureServiceBusQueue("send_and_receive");

        opts.PublishAllMessages().ToAzureServiceBusQueue("send_and_receive");
    }).StartAsync();
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Transports/Azure/Wolverine.AzureServiceBus.Tests/end_to_end.cs#L74-L88' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_disable_system_queues_in_azure_service_bus' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->








