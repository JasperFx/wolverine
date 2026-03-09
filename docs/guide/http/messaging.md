# Sending Messages from HTTP Endpoints

::: tip
You can also use `IMessageBus` directly from an MVC Controller or a Minimal API method, but
you'll be responsible for the outbox mechanics that Wolverine takes care of for you in Wolverine
message handlers or Wolverine http endpoints.
:::

So there's absolutely nothing stopping you from just using `IMessageBus` as an injected
dependency to a Wolverine HTTP endpoint method to publish messages like this sample:

<!-- snippet: sample_publishing_cascading_messages_from_Http_endpoint_with_IMessageBus -->
<a id='snippet-sample_publishing_cascading_messages_from_http_endpoint_with_imessagebus'></a>
```cs
// This would have an empty response and a 204 status code
[WolverinePost("/spawn3")]
public static async ValueTask SendViaMessageBus(IMessageBus bus)
{
    await bus.PublishAsync(new HttpMessage1("foo"));
    await bus.PublishAsync(new HttpMessage2("bar"));
}
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Http/WolverineWebApi/MessageHandlers.cs#L49-L59' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_publishing_cascading_messages_from_http_endpoint_with_imessagebus' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

But of course there's some other alternatives to directly using `IMessageBus` by utilizing Wolverine's [cascading messages](/guide/handlers/cascading)
capability and the ability to customize how Wolverine handles return values. 

## Sending or publishing directly from URL

::: tip
It's an imperfect world, and the following code sample has to deserialize the incoming HTTP
request to the message body, then publishes that directly to Wolverine which might turn around
and serialize it back to a binary.
:::

The following syntax shows a shorthand mechanism to map an incoming HTTP request message type
to be immediately published to Wolverine without any need for additional Wolverine endpoints or MVC controllers.
Note that this mechanism will return an empty body with a status code of 202 to denote future processing.

<!-- snippet: sample_send_http_methods_directly_to_Wolverine -->
<a id='snippet-sample_send_http_methods_directly_to_wolverine'></a>
```cs
var builder = WebApplication.CreateBuilder();

builder.Host.UseWolverine();

var app = builder.Build();

app.MapWolverineEndpoints(opts =>
{
    opts.SendMessage<CreateOrder>("/orders/create", chain =>
    {
        // You can make any necessary metadata configurations exactly
        // as you would for Minimal API endpoints with this syntax
        // to fine tune OpenAPI generation or security
        chain.Metadata.RequireAuthorization();
    });
    opts.SendMessage<ShipOrder>(HttpMethod.Put, "/orders/ship");
});

// and the rest of your application configuration and bootstrapping
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Http/WolverineWebApi/Samples/SendingMessages.cs#L11-L33' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_send_http_methods_directly_to_wolverine' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

On the other hand, the `PublishAsync()` method will send a message if there is a known subscriber and ignore the message if there is no subscriber (as explained in [sending or publishing Messages](/guide/messaging/message-bus#sending-or-publishing-messages)):

<!-- snippet: sample_publish_http_methods_directly_to_Wolverine -->
<a id='snippet-sample_publish_http_methods_directly_to_wolverine'></a>
```cs
var builder = WebApplication.CreateBuilder();

builder.Host.UseWolverine();

var app = builder.Build();

app.MapWolverineEndpoints(opts =>
{
    opts.PublishMessage<CreateOrder>("/orders/create", chain =>
    {
        // You can make any necessary metadata configurations exactly
        // as you would for Minimal API endpoints with this syntax
        // to fine tune OpenAPI generation or security
        chain.Metadata.RequireAuthorization();
    });
    opts.PublishMessage<ShipOrder>(HttpMethod.Put, "/orders/ship");
});

// and the rest of your application configuration and bootstrapping
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Http/WolverineWebApi/Samples/PublishingMessages.cs#L11-L33' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_publish_http_methods_directly_to_wolverine' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

Middleware policies from Wolverine.Http are applicable to these endpoints, so for example, it's feasible to use
the FluentValidation middleware for HTTP with these forwarding endpoints.

## Cascading Messages

To utilize *cascaded messages* from HTTP endpoints (messages that are returned form the HTTP handler method), you have two main options.
First, you can use Wolverine's `OutgoingMessages` collection as a tuple return value that makes it clear to Wolverine
that this collection of objects is meant to be cascaded messages that are published upon the success of this HTTP endpoint.

Here's an example:

<!-- snippet: sample_spawning_messages_from_http_endpoint_via_OutgoingMessages -->
<a id='snippet-sample_spawning_messages_from_http_endpoint_via_outgoingmessages'></a>
```cs
// This would have a string response and a 200 status code
[WolverinePost("/spawn")]
public static (string, OutgoingMessages) Post(SpawnInput input)
{
    var messages = new OutgoingMessages
    {
        new HttpMessage1(input.Name),
        new HttpMessage2(input.Name),
        new HttpMessage3(input.Name),
        new HttpMessage4(input.Name)
    };

    return ("got it", messages);
}
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Http/WolverineWebApi/MessageHandlers.cs#L73-L90' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_spawning_messages_from_http_endpoint_via_outgoingmessages' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

Otherwise, if you want to make it clearer from the signature of your HTTP handler method what messages are cascaded
and there's no variance in the type of messages published, you can use additional tuple return values like this:

<!-- snippet: sample_publishing_cascading_messages_from_Http_endpoint -->
<a id='snippet-sample_publishing_cascading_messages_from_http_endpoint'></a>
```cs
// This would have an empty response and a 204 status code
[EmptyResponse]
[WolverinePost("/spawn2")]
public static (HttpMessage1, HttpMessage2) Post()
{
    return new(new HttpMessage1("foo"), new HttpMessage2("bar"));
}
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Http/WolverineWebApi/MessageHandlers.cs#L61-L71' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_publishing_cascading_messages_from_http_endpoint' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->
