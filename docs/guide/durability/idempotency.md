# Idempotent Message Delivery

::: tip
There is nothing you need to do to opt into idempotent, no more than once message deduplication other than to be using the durable inbox
on any Wolverine listening endpoint where you want this behavior. 
:::

When applying the [durable inbox](/guide/durability/#using-the-inbox-for-incoming-messages) to [message listeners](/guide/messaging/listeners), you also get a no more than once, 
[idempotent](https://en.wikipedia.org/wiki/Idempotence) message delivery guarantee. This means that Wolverine will discard
any received message that it can detect has been previously handled. Wolverine does this with its durable inbox storage to check on receipt of a 
new message if that message is already known by its Wolverine identifier. 

Instead of immediately deleting message storage for a successfully completed message, Wolverine merely marks that the message is handled and keeps
that message in storage for a default of 5 minutes to protect against duplicate incoming messages. To override that setting, you have this option:

<!-- snippet: sample_configuring_KeepAfterMessageHandling -->
<a id='snippet-sample_configuring_keepaftermessagehandling'></a>
```cs
using var host = await Host.CreateDefaultBuilder()
    .UseWolverine(opts =>
    {
        // The default is 5 minutes, but if you want to keep
        // messages around longer (or shorter) in case of duplicates,
        // this is how you do it
        opts.Durability.KeepAfterMessageHandling = 10.Minutes();
    }).StartAsync();
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Persistence/PersistenceTests/Samples/DocumentationSamples.cs#L195-L206' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_configuring_keepaftermessagehandling' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->
