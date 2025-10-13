# Using SignalR <Badge type="tip" text="5.0" />

::: info
The SignalR transport has been requested several times, but finally got built specifically for the forthcoming
"CritterWatch" product that will be used to monitor and manage Wolverine applications. In other words, the Wolverine
team has heavily dog-fooded this feature.
:::

::: tip
Much of the sample code is taken from a runnable sample application in the Wolverine codebase called [WolverineChat](https://github.com/JasperFx/wolverine/tree/main/src/Samples/WolverineChat).
:::

The [SignalR library](https://dotnet.microsoft.com/en-us/apps/aspnet/signalr) from Microsoft isn't hard to use from Wolverine for simplistic WebSockets
or Server Side Events usage , but what if you want a server side
application to exchange any number of different messages between a browser (or other WebSocket client because that's
actually possible) and your server side code in a systematic way? To that end, Wolverine now supports a first class messaging transport
for SignalR. To get started, just add a Nuget reference to the `WolverineFx.SignalR` library:

```bash
dotnet add package WolverineFx.SignalR
```

## Configuring the Server

::: tip
Wolverine.SignalR does not require any usage of Wolverine.HTTP, but these two libraries can certainly be used in the same
application as well.
:::

The Wolverine.SignalR library sets up a single SignalR `Hub` type in your system (`WolverineHub`) that will be used to both send and 
receive messages from the browser. To set up both the SignalR transport and the necessary SignalR services in your DI container,
use this syntax in the `Program` file of your web application:

<!-- snippet: sample_configuring_signalr_on_server_side -->
<a id='snippet-sample_configuring_signalr_on_server_side'></a>
```cs
builder.UseWolverine(opts =>
{
    // This is the only single line of code necessary
    // to wire SignalR services into Wolverine itself
    // This does also call IServiceCollection.AddSignalR()
    // to register DI services for SignalR as well
    opts.UseSignalR();
    
    // Using explicit routing to send specific
    // messages to SignalR
    opts.Publish(x =>
    {
        // WolverineChatWebSocketMessage is a marker interface
        // for messages within this sample application that
        // is simply a convenience for message routing
        x.MessagesImplementing<WolverineChatWebSocketMessage>();
        x.ToSignalR();
    });
});
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Samples/WolverineChat/Program.cs#L11-L33' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_configuring_signalr_on_server_side' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

That handles the Wolverine configuration and the SignalR service registrations, but you will also need to map
an HTTP route for the SignalR hub with this Wolverine.SignalR helper:

<!-- snippet: sample_using_map_wolverine_signalrhub -->
<a id='snippet-sample_using_map_wolverine_signalrhub'></a>
```cs
var app = builder.Build();

app.UseRouting();

app.UseAuthorization();

app.MapStaticAssets();
app.MapRazorPages()
    .WithStaticAssets();

// This line puts the SignalR hub for Wolverine at the 
// designated route for your clients
app.MapWolverineSignalRHub("/api/messages");

return await app.RunJasperFxCommands(args);
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Samples/WolverineChat/Program.cs#L37-L55' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_using_map_wolverine_signalrhub' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

## Messages and Serialization

For the message routing above, you'll notice that I utilized a marker interface just to facilitate message routing 
like this:

<!-- snippet: sample_WolverineChatWebSocketMessage -->
<a id='snippet-sample_wolverinechatwebsocketmessage'></a>
```cs
// Marker interface for the sample application just to facilitate
// message routing
public interface WolverineChatWebSocketMessage : WebSocketMessage;
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Samples/WolverineChat/Server.cs#L7-L13' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_wolverinechatwebsocketmessage' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

The Wolverine `WebSocketMessage` marker interface does have a little bit of impact in that:

1. It implements the `IMessage` interface that's just a helper for [Wolverine to discover message types](/guide/messages.html#message-discovery)
   in your application upfront for diagnostics or upfront resource creation
2. By marking your message types as `WebSocketMessage`, it changes [Wolverine's message type name](/guide/messages.html#message-type-name-or-alias) rules to using a [Kebab-cased version of the message type name](https://developer.mozilla.org/en-US/docs/Glossary/Kebab_case) 

For example, these three message types:

<!-- snippet: sample_signalr_message_types -->
<a id='snippet-sample_signalr_message_types'></a>
```cs
public record ChatMessage(string User, string Text) : WolverineChatWebSocketMessage;
public record ResponseMessage(string User, string Text) : WolverineChatWebSocketMessage;

public record Ping(int Number) : WolverineChatWebSocketMessage;
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Samples/WolverineChat/Server.cs#L15-L22' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_signalr_message_types' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

will result in these message type names according to Wolverine:

| .NET Type         | Wolverine Message Type Name |
|-------------------|-----------------------------|
| `ChatMessage`     | "chat_message"              |
| `ResponseMessage` | "response_message"          |
| `Ping`            | "ping"                      |

That message type name is important because the Wolverine SignalR transport uses and expects a very light [CloudEvents](https://cloudevents.io/) wrapper around
the raw message being sent to the client and received from the browser. Here's an example of the JSON payload for the
`ChatMessage` message:

```json
{
  "type": "chat_message",
  "data": {
    "user": "Hank",
    "text": "Hey"
  }
}
```

The only elements that are mandatory are the `type` node that should be the Wolverine message type name and `data` that 
is the actual message serialized by JSON. Wolverine will send the full CloudEvents envelope structure because it's
reusing the envelope mapping from [our CloudEvents interoperability](/tutorials/interop.html#interop-with-cloudevents), but the browser code **only** needs to send `type`
and `data`. 

The actual JSON serialization in the SignalR transport is isolated from the rest of Wolverine and uses this default
`System.Text.Json` configuration:

<!-- snippet: sample_signalr_default_json_configuration -->
<a id='snippet-sample_signalr_default_json_configuration'></a>
```cs
JsonOptions = new(JsonSerializerOptions.Web) { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
JsonOptions.Converters.Add(new JsonStringEnumConverter());
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Transports/SignalR/Wolverine.SignalR/Internals/SignalRTransport.cs#L26-L31' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_signalr_default_json_configuration' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

But of course, if you needed to override the JSON serialization for whatever reason, you can just push in a 
different `JsonSerializerOptions` like this:

<!-- snippet: sample_overriding_signalr_serialization -->
<a id='snippet-sample_overriding_signalr_serialization'></a>
```cs
var builder = WebApplication.CreateBuilder();

builder.UseWolverine(opts =>
{
    // Just showing you how to override the JSON serialization
    opts.UseSignalR().OverrideJson(new JsonSerializerOptions
    {
        IgnoreReadOnlyProperties = false
    });
});
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Transports/SignalR/Wolverine.SignalR.Tests/SampleCode.cs#L10-L23' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_overriding_signalr_serialization' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

## Interacting with the Server from the Browser

It's not mandatory, but in developing and dogfooding the Wolverine.SignalR transport, we've found it helpful to use
the actual [signalr Javascript library](https://learn.microsoft.com/en-us/aspnet/core/signalr/javascript-client?view=aspnetcore-9.0&tabs=visual-studio) and
our sample SignalR application uses that library for the browser to server communication.

```js
"use strict";

// Connect to the server endpoint
var connection = new signalR.HubConnectionBuilder().withUrl("/api/messages").build();

//Disable the send button until connection is established.
document.getElementById("sendButton").disabled = true;

// Receiving messages from the server
connection.on("ReceiveMessage", function (json) {
   // Note that you will need to deserialize the raw JSON
   // string
   const message = JSON.parse(json);

   // The client code will need to effectively do a logical
   // switch on the message.type. The "real" message is 
   // the data element
   if (message.type == 'ping'){
      console.log("Got ping " + message.data.number);
   }
   else{
      const li = document.createElement("li");
      document.getElementById("messagesList").appendChild(li);
      li.textContent = `${message.data.user} says ${message.data.text}`;
   }
});

connection.start().then(function () {
   document.getElementById("sendButton").disabled = false;
}).catch(function (err) {
   return console.error(err.toString());
});

document.getElementById("sendButton").addEventListener("click", function (event) {
   const user = document.getElementById("userInput").value;
   const text = document.getElementById("messageInput").value;

   // Remember that we need to wrap the raw message in this slim
   // CloudEvents wrapper
   const message = {type: 'chat_message', data: {'text': text, 'user': user}};

   // The WolverineHub method to call is ReceiveMessage with a single argument
   // for the raw JSON
   connection.invoke("ReceiveMessage", JSON.stringify(message)).catch(function (err) {
      return console.error(err.toString());
   });
   event.preventDefault();
});
```

Note that the method `ReceiveMessage` is hard coded into the `WolverineHub` service.

Our vision for this usage is that you probably integrate directly with a client side state tracking tool like [Pinia](https://pinia.vuejs.org/)
(how we're using the SignalR transport to build "CritterWatch").

## Sending Messages to SignalR

For the most part, sending a message to SignalR is just like sending messages with any other transport like this sample:

<!-- snippet: sample_signalr_pinging -->
<a id='snippet-sample_signalr_pinging'></a>
```cs
public class Pinging : BackgroundService
{
    private readonly IWolverineRuntime _runtime;

    public Pinging(IWolverineRuntime runtime)
    {
        _runtime = runtime;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var number = 0;
        while (!stoppingToken.IsCancellationRequested)
        {
            await Task.Delay(1.Seconds(), stoppingToken);
            
            // This is being published to all connected SignalR
            // applications
            await new MessageBus(_runtime).PublishAsync(new Ping(++number));
        }
    }
}
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Samples/WolverineChat/Server.cs#L32-L57' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_signalr_pinging' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

The call above will occasionally send a `Ping` message to all connected clients. But of course, you'll frequently want
to more selectively send messages to reply to the current connection or maybe to a specific group.

If you are handling a message that originated from SignalR, you can send a response back to the originating connection
like this:

<!-- snippet: sample_sending_response_to_originating_signalr_caller -->
<a id='snippet-sample_sending_response_to_originating_signalr_caller'></a>
```cs
public record RequestSum(int X, int Y) : WebSocketMessage;
public record SumAnswer(int Value) : WebSocketMessage;

public static class RequestSumHandler
{
    public static ResponseToCallingWebSocket<SumAnswer> Handle(RequestSum message)
    {
        return new SumAnswer(message.X + message.Y)
            
            // This extension method will wrap the raw message
            // with some helpers that will 
            .RespondToCallingWebSocket();
    }
}
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Transports/SignalR/Wolverine.SignalR.Tests/SampleCode.cs#L27-L44' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_sending_response_to_originating_signalr_caller' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

In the next section we'll learn a bit more about working with SignalR groups.

## SignalR Groups

One of the powerful features of SignalR is being able to work with [groups of connections](https://learn.microsoft.com/en-us/aspnet/signalr/overview/guide-to-the-api/working-with-groups).
The SignalR transport currently has some simple support for managing and publishing to groups. Let's say you have
these web socket messages in your system:

snippet: sample_messages_related_to_signalr_groups

The following code is a set of simplistic message handlers that handle these messages with some SignalR connection
group mechanics:

snippet: sample_group_mechanics_with_signalr

In the code above:

* `AddConnectionToGroup` and `RemoveConnectionToGroup` are both examples of Wolverine ["side effects"](/guide/handlers/side-effects.html) that are specific to adding or removing the
  current SignalR connection (whichever connection originated the message and where the SignalR transport received the message)
* `ToWebSocketGroup(group name)` is an extension method in Wolverine.SignalR that restricts the message being sent to SignalR to only being sent to connections in that named group

## SignalR Client Transport

::: tip
If you want to use the .NET SignalR Client for test automation, just know that you will need to bootstrap the service
that actually hosts SignalR with the full stack including Kestrel. `WebApplicationFactory` will not be suitable for this
type of integration testing through SignalR.
:::

Wolverine.SignalR is actually two transports in one library! There is also a full fledged messaging transport built
around the [.NET SignalR client](https://learn.microsoft.com/en-us/aspnet/core/signalr/dotnet-client?view=aspnetcore-9.0&tabs=visual-studio) that we've used extensively for test automation, but could technically be used as 
a "real" messaging transport. The SignalR Client transport was built specifically to enable end to end testing against
a Wolverine server that hosts SignalR itself. The SignalR Client transport will use the same CloudEvents mechanism to
send and receive messages from the main Wolverine SignalR transport and is 100% compatible.

If you wanted to use the SignalR client as a "real" messaging transport, you could do that like this sample:

snippet: sample_bootstrap_signalr_client_for_realsies

Or a little more simply, if you are just using this for test automation, you would need to give it the port number where
your SignalR hosting service is running on the local computer:

snippet: sample_bootstrap_signalr_client_for_local

To make this a little more concrete, here's a little bit of the test harness setup we used to test the Wolverine.SignalR
transport:

snippet: sample_signalr_client_test_harness_setup

In the same test harness class, we bootstrap new `IHost` instances with the SignalR Client to mimic browser client
communication like this:

snippet: sample_bootstrapping_signalr_client_in_test

The key point here is that we stood up the service using a port number for Kestrel, then stood up `IHost` instances for
a Wolverine application using the SignalR Client using the same port number for easy connectivity.

And of course, after all of that we should probably talk about how to publish messages via the SignalR Client. Fortunately,
there's really nothing to it. You merely need to invoke the normal `IMessageBus.PublishAsync()` APIs that you would use
for any messaging. In the sample test below, we're utilizing the [tracked session](https://wolverinefx.net/guide/testing.html#integration-testing-with-tracked-sessions) functionality as normal to send a 
message from the `IHost` hosting the SignalR Client transport and expect it to be successfully handled in the `IHost` 
for our actual SignalR server:

snippet: sample_end_to_end_test_with_signalr

*Conveniently enough as I write this documentation today using existing test code, Hollywood Brown had a huge
game last night. Go Chiefs!*

## Web Socket "Sagas"

::: info
The functionality described in this section was specifically built for "CritterWatch" where a browser request kicks off
a "scatter/gather" series of messages from CritterWatch to other Wolverine services and finally back to the originating
browser client. 
:::

Let's say that you have a workflow in your system something like:

1. The browser makes a web socket call to the server to request some information or take a long running action
2. The server application needs to execute several messages or even call out to additional Wolverine services
3. Once the server application has finally completed the work that the client requested, the server needs to send
   a message to the originating SignalR connection with the status of the long running activity or the data that the original client
   requested

The SignalR transport can leverage some of Wolverine's built in saga tracking to be able to route the eventual Web Socket
response back to the originating caller even if the work required intermediate steps. The easiest way to enroll in this
behavior today is the usage of the `[EnlistInCurrentConnectionSaga]` that should be on either 




