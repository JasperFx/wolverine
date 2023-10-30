# Wolverine and Serverless

::: tip
No telling when this would happen (Spring 2024?), but there is an "ultra efficient" serverless model planned for Wolverine
that will lean even heavier into code generation as a way to optimize its usage within serverless functions. Track that [forthcoming
work on GitHub](https://github.com/JasperFx/wolverine/issues/34).
:::

Wolverine was very much originally envisioned for usage in long running processes, and as such, wasn't initially well suited to
serverless technologies like [Azure Functions](https://azure.microsoft.com/en-us/products/functions) or [AWS Lambda functions](https://aws.amazon.com/pm/lambda).

If you're choosing to use Wolverine HTTP endpoints or message handling as part of a serverless function, we have three
main suggestions about making Wolverine be more successful:

1. Make any outgoing [message endpoints](/guide/runtime.html#endpoint-types) be *Inline* so that messages are sent immediately
2. Utilize the new *Serverless* optimized mode
3. Absolutely take advantage of [pre-generated types]() to cut down the all important cold start problem with serverless functions

## Serverless Mode

::: tip
Wolverine's [Transactional Inbox/Outbox](/guide/durability/) is very unsuitable for usage within serverless functions, so you'll definitely
want to disable it through the mode shown below
:::

First off, let's say that you want to use the transactional
middleware for either Marten or EF Core within your serverless functions. That's all good, but you will want to turn off
all of Wolverine's transactional inbox/outbox functionality with this setting that was added in 1.10.0:

<!-- snippet: sample_configuring_the_serverless_mode -->
<a id='snippet-sample_configuring_the_serverless_mode'></a>
```cs
using var host = await Host.CreateDefaultBuilder()
    .UseWolverine(opts =>
    {
        opts.Services.AddMarten("some connection string")

            // This adds quite a bit of middleware for 
            // Marten
            .IntegrateWithWolverine();
        
        // You want this maybe!
        opts.Policies.AutoApplyTransactions();
        
        
        // But wait! Optimize Wolverine for usage within Serverless
        // and turn off the heavy duty, background processes
        // for the transactional inbox/outbox
        opts.Durability.Mode = DurabilityMode.Serverless;
    }).StartAsync();
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Samples/DocumentationSamples/DurabilityModes.cs#L14-L35' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_configuring_the_serverless_mode' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

## Pre-Generate All Types

The runtime code generation that Wolverine does comes with a potentially non-trivial "cold start" problem with its first
usage. In serverless architectures, that's probably intolerable. With Wolverine, you can bypass that cold start problem
by opting into [pre-generated types](/guide/codegen.html#generating-code-ahead-of-time).

## Use Inline Endpoints

If you are using Wolverine to send cascading messages from handlers in serverless functions, you will want to use
*Inline* endpoints where the messages are sent immediately without any background processing as would be normal with *Buffered* or *Durable*
endpoints:

<!-- snippet: sample_usage_of_send_inline -->
<a id='snippet-sample_usage_of_send_inline'></a>
```cs
.UseWolverine(opts =>
{
    opts.UseRabbitMq().AutoProvision().AutoPurgeOnStartup();
    opts
        .PublishAllMessages()
        .ToRabbitQueue(queueName)
        
        // This option is important inside of Serverless functions
        .SendInline();
})
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Transports/RabbitMQ/Wolverine.RabbitMQ.Tests/Bugs/Bug_189_fails_if_there_are_many_messages_in_queue_on_startup.cs#L20-L33' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_usage_of_send_inline' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->
