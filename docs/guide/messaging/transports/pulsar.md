# Using Pulsar <Badge type="tip" text="3.0" />

::: info
Fun fact, the Pulsar transport was actually the very first messaging broker to be supported
by Jasper/Wolverine, but for whatever reason, wasn't officially released until Wolverine 3.0. 
:::

## Installing

To use [Apache Pulsar](https://pulsar.apache.org/) as a messaging transport with Wolverine, first install the `WolverineFx.Pulsar` library via nuget to your project. Behind the scenes, this package uses the [DotPulsar client library](https://pulsar.apache.org/docs/next/client-libraries-dotnet/) managed library for accessing Pulsar brokers.

```bash
dotnet add WolverineFx.Pulsar
```

To connect to Pulsar and configure senders and listeners, use this syntax:

<!-- snippet: sample_configuring_pulsar -->
<a id='snippet-sample_configuring_pulsar'></a>
```cs
var builder = Host.CreateApplicationBuilder();
builder.UseWolverine(opts =>
{
    opts.UsePulsar(c =>
    {
        var pulsarUri = builder.Configuration.GetValue<Uri>("pulsar");
        c.ServiceUrl(pulsarUri);
        
        // Any other configuration you want to apply to your
        // Pulsar client
    });

    // Publish messages to a particular Pulsar topic
    opts.PublishMessage<Message1>()
        .ToPulsarTopic("persistent://public/default/one")
        
        // And all the normal Wolverine options...
        .SendInline();

    // Listen for incoming messages from a Pulsar topic
    opts.ListenToPulsarTopic("persistent://public/default/two")
        .SubscriptionName("two")
        .SubscriptionType(SubscriptionType.Exclusive)
        
        // And all the normal Wolverine options...
        .Sequential();

    // Listen for incoming messages from a Pulsar topic with a shared subscription and using RETRY and DLQ queues
    opts.ListenToPulsarTopic("persistent://public/default/three")
        .WithSharedSubscriptionType()
        .DeadLetterQueueing(new DeadLetterTopic(DeadLetterTopicMode.Native))
        .RetryLetterQueueing(new RetryLetterTopic([TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(5)]))
        .Sequential();
});
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Transports/Pulsar/Wolverine.Pulsar.Tests/DocumentationSamples.cs#L12-L50' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_configuring_pulsar' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

The topic name format is set by Pulsar itself, and you can learn more about its format in [Pulsar Topics](https://pulsar.apache.org/docs/next/concepts-messaging/#topics). 

::: info
Depending on demand, the Pulsar transport will be enhanced to support conventional routing topologies and more advanced
topic routing later.
::: 

## Read Only Subscriptions <Badge type="tip" text="3.13" />

As part of Wolverine's "Requeue" error handling action, the Pulsar transport tries to quietly create a matching sender
for each Pulsar topic it's listening to. Great, but that will blow up if your application only has receive-only permissions
to Pulsar. In this case, you probably want to disable Pulsar requeue actions altogether with this setting:

<!-- snippet: sample_disable_requeue_for_pulsar -->
<a id='snippet-sample_disable_requeue_for_pulsar'></a>
```cs
var builder = Host.CreateApplicationBuilder();
builder.UseWolverine(opts =>
{
    opts.UsePulsar(c =>
    {
        var pulsarUri = builder.Configuration.GetValue<Uri>("pulsar");
        c.ServiceUrl(pulsarUri);
    });

    // Listen for incoming messages from a Pulsar topic
    opts.ListenToPulsarTopic("persistent://public/default/two")
        .SubscriptionName("two")
        .SubscriptionType(SubscriptionType.Exclusive)
        
        // Disable the requeue for this topic
        .DisableRequeue()
        
        // And all the normal Wolverine options...
        .Sequential();

    // Disable requeue for all Pulsar endpoints
    opts.DisablePulsarRequeue();
});
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Transports/Pulsar/Wolverine.Pulsar.Tests/DocumentationSamples.cs#L55-L81' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_disable_requeue_for_pulsar' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

If you have an application that has receive only access to a subscription but not permissions to publish to Pulsar,
you cannot use the Wolverine "Requeue" error handling policy.

### Subscription behavior when closing connection

By default, the Pulsar transport will automatically close the subscription when the endpoints is being stopped.
If the subscription is created for you, and should be kept after application shut down, you can change this behavior.

<!-- snippet: sample_pulsar_unsubscribe_on_close -->
<a id='snippet-sample_pulsar_unsubscribe_on_close'></a>
```cs
var builder = Host.CreateApplicationBuilder();
builder.UseWolverine(opts =>
{
    opts.UsePulsar(c =>
    {
        var pulsarUri = builder.Configuration.GetValue<Uri>("pulsar");
        c.ServiceUrl(pulsarUri);
    });

    // Disable unsubscribe on close for all Pulsar endpoints
    opts.UnsubscribePulsarOnClose(PulsarUnsubscribeOnClose.Disabled);
});
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Transports/Pulsar/Wolverine.Pulsar.Tests/DocumentationSamples.cs#L86-L101' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_pulsar_unsubscribe_on_close' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

## Interoperability

::: tip
Also see the more generic [Wolverine Guide on Interoperability](/tutorials/interop)
:::

Pulsar interoperability is done through the `IPulsarEnvelopeMapper` interface.
