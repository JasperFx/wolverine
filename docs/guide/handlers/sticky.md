# Sticky Handler to Endpoint Assignments <Badge type="tip" text="3.0" />

::: info
The original behavior of Wolverine and the way it combines all handlers for a given message type into one logical
transaction was an explicit design choice in a predecessor tool named _FubuTransportation_ and was carried through
into _Jasper_ and finally into today's Wolverine. That decision absolutely made sense in the context of the original
system that _FubuTransportation_ was designed for, but maybe not so much today. Such is software development.
:::

By default, Wolverine will combine all the discovered handlers for a certain message type in one logical transaction. 
Another Wolverine default behavior is that there is no explicit mapping of handler types to listening endpoints or local
endpoints, which was a very conscious decision to simplify Wolverine usage compared to older .NET messaging frameworks. 

At other times though, you may want the same message (usually a logical "event" message) to be handled separately by
two or more distinct message handlers and even be routed to separate local queues. In another instance, you may want
to have separate message handlers apply based on where the message is received from. In all cases, this is what the "sticky handler"
functionality is meant to accomplish.

Let's start with a simple example and say that you have a message type called `StickyMessage` that when published should
be handled completely separately by two different handlers performing two different logical operations using the same
message as an input.

<!-- snippet: sample_StickyMessage -->
<a id='snippet-sample_stickymessage'></a>
```cs
public class StickyMessage;
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Testing/CoreTests/Acceptance/sticky_message_handlers.cs#L234-L238' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_stickymessage' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

And we're going to handle that `StickyMessage` message separately with two different handler types:

<!-- snippet: sample_using_sticky_handler_attribute -->
<a id='snippet-sample_using_sticky_handler_attribute'></a>
```cs
[StickyHandler("blue")]
public static class BlueStickyHandler
{
    public static StickyMessageResponse Handle(StickyMessage message, Envelope envelope)
    {
        return new StickyMessageResponse("blue", message, envelope.Destination);
    }
}

[StickyHandler("green")]
public static class GreenStickyHandler
{
    public static StickyMessageResponse Handle(StickyMessage message, Envelope envelope)
    {
        return new StickyMessageResponse("green", message, envelope.Destination);
    }
}
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Testing/CoreTests/Acceptance/sticky_message_handlers.cs#L240-L260' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_using_sticky_handler_attribute' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

::: tip
`[StickyHandler]` can be used on either the handler class or the handler method
:::

I'd ask you to notice the usage of the `[StickyHandler]` attribute on the two message handlers. In this case,
Wolverine sees these attributes on the handler types and "knows" to only execute that message handler on the endpoint
named in the attribute. The endpoint resolution rules are:

1. Try to find and existing endpoint with the same name and "stick" the handler type to that endpoint
2. If no endpoint with that name exists, create a new local queue endpoint _and_ create a routing rule for that message type
   to that local queue

As an example of an explicitly named endpoint, see this sample:

<!-- snippet: sample_named_listener_endpoint -->
<a id='snippet-sample_named_listener_endpoint'></a>
```cs
using var host = await Host.CreateDefaultBuilder()
    .UseWolverine(opts =>
    {
        // I'm explicitly configuring an incoming TCP
        // endpoint named "blue"
        opts.ListenAtPort(4000).Named("blue");
    }).StartAsync();
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Testing/CoreTests/Acceptance/sticky_message_handlers.cs#L172-L182' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_named_listener_endpoint' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

With all of that being said, the end result of the two `StickyMessage` handlers that are marked with `[StickyHandler]`
is that when a `StickyMessage` message is published in the system, it will be:

1. Published to a local queue named "green" where it will be handled by the `GreenStickyHandler` handler
2. Published to a local queue named "blue" where it will be handled by the `BlueStickyHandler` handler

In both cases, the message is tracked separately in terms of queueing, failures, and retries.

::: tip
If there are multiple handlers for the same message and only some of the handlers have explicit "sticky" rules, the 
handlers with no configured "sticky" rules will be executed if that message is published to any other endpoint. Call these
the "leftovers"
:::

It's also possible -- and maybe advantageous -- to define the stickiness with the fluent interface directly against
the listening endpoints. In the case of wanting to handle external messages separately depending on where they come from,
you can tag the handler stickiness to an endpoint like so:

<!-- snippet: sample_sticky_handlers_by_endpoint_with_fluent_interface -->
<a id='snippet-sample_sticky_handlers_by_endpoint_with_fluent_interface'></a>
```cs
using var host = await Host.CreateDefaultBuilder()
    .UseWolverine(opts =>
    {
        opts.ListenAtPort(400)
            // This handler type should be executed at this listening
            // endpoint, but other handlers for the same message type
            // should not
            .AddStickyHandler(typeof(GreenStickyHandler));
        
        opts.ListenAtPort(5000)
            // Likewise, the same StickyMessage received at this
            // endpoint should be handled by BlueStickHandler
            .AddStickyHandler(typeof(BlueStickyHandler));

    }).StartAsync();
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Testing/CoreTests/Acceptance/sticky_message_handlers.cs#L187-L205' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_sticky_handlers_by_endpoint_with_fluent_interface' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

## Configuring Local Queues <Badge type="tip" text="3.7" />

There is a world of reasons why you might want to fine tune the behavior of local queues (sequential ordering? parallelism? circuit breakers?), but the 
"sticky" handler usage did make it a little harder to configure the exact right local queue for a sticky handler. To alleviate that, see the 
[IConfigureLocalQueue](/guide/messaging/transports/local.html#using-iconfigurelocalqueue-to-configure-local-queues) usage. 


