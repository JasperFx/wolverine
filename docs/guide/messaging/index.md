# Wolverine as Messaging Bus

There's certainly some value in Wolverine just being a command bus running inside of a single process, but now
it's time to utilize Wolverine to both publish and process messages received through external infrastructure like [Rabbit MQ](https://www.rabbitmq.com/)
or [Pulsar](https://pulsar.apache.org/).

## Terminology

To put this into perspective, here's how a Wolverine application could be connected to the outside world:

![Wolverine Messaging Architecture](/WolverineMessaging.png)

:::tip
The diagram above should just say "Message Handler" as Wolverine makes no differentiation between commands or events, but Jeremy is being too lazy to fix the diagram.
:::

Before going into any kind of detail about how to use Wolverine messaging, let's talk about some terminology:

* *Transport* -- This refers to the support within Wolverine for external messaging infrastructure tools like Rabbit MQ or Pulsar
* *Endpoint* -- A Wolverine connection to some sort of external resource like a Rabbit MQ exchange or a Pulsar or Kafka topic. The [Async API](https://www.asyncapi.com/) specification refers to this as a *channel*, and Wolverine may very well change its nomenclature in the future to be consistent with Async API
* *Sending Agent* -- You won't use this directly in your own code, but Wolverine's internal adapters to publish outgoing messages to transport endpoints
* *Listener* -- Again, an internal detail of Wolverine that receives messages from external transport endpoints, and mediates between the transports and executing the message handlers
* *Message Store* -- Database storage for Wolverine's [inbox/outbox persistent messaging](/guide/persistence/)
* *Durability Agent* -- An internal subsystem in Wolverine that runs in a background service to interact with the message store for Wolverine's [transactional inbox/outbox](https://microservices.io/patterns/data/transactional-outbox.html) functionality

## Ping/Pong Sample

To show off some of the messaging, let's just build [a very simple "Ping/Pong" example](https://github.com/JasperFx/wolverine/tree/master/src/Samples/PingPong) that will exchange messages between two small .NET processes.

First off, I'm going to build out a very small shared library just to hold the messages we're going to exchange:

<!-- snippet: sample_PingPongMessages -->
<a id='snippet-sample_pingpongmessages'></a>
```cs
public class Ping
{
    public int Number { get; set; }
}

public class Pong
{
    public int Number { get; set; }
}
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Samples/PingPong/Messages/Messages.cs#L3-L15' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_pingpongmessages' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

And next, I'll start a small *Pinger* service with the `dotnet new worker` template. There's just three pieces of code, starting with the boostrapping code:

<!-- snippet: sample_BootstrappingPinger -->
<a id='snippet-sample_bootstrappingpinger'></a>
```cs
using Wolverine;
using Wolverine.Transports.Tcp;
using Messages;
using Oakton;
using Pinger;

return await Host.CreateDefaultBuilder(args)
    .UseWolverine(opts =>
    {
        // Using Wolverine's built in TCP transport

        // listen to incoming messages at port 5580
        opts.ListenAtPort(5580);

        // route all Ping messages to port 5581
        opts.PublishMessage<Ping>().ToPort(5581);

        // Registering the hosted service here, but could do
        // that with a separate call to IHostBuilder.ConfigureServices()
        opts.Services.AddHostedService<Worker>();
    })
    .RunOaktonCommands(args);
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Samples/PingPong/Pinger/Program.cs#L1-L26' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_bootstrappingpinger' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

and the `Worker` class that's just going to publish a new `Ping` message once a second:

<!-- snippet: sample_PingPong_Worker -->
<a id='snippet-sample_pingpong_worker'></a>
```cs
using Wolverine;
using Messages;

namespace Pinger;

public class Worker : BackgroundService
{
    private readonly ILogger<Worker> _logger;
    private readonly IMessagePublisher _publisher;

    public Worker(ILogger<Worker> logger, IMessagePublisher publisher)
    {
        _logger = logger;
        _publisher = publisher;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var pingNumber = 1;

        while (!stoppingToken.IsCancellationRequested)
        {

            await Task.Delay(1000, stoppingToken);
            _logger.LogInformation("Sending Ping #{Number}", pingNumber);
            await _publisher.PublishAsync(new Ping { Number = pingNumber });
            pingNumber++;
        }
    }
}
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Samples/PingPong/Pinger/Worker.cs#L1-L35' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_pingpong_worker' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

and lastly a message handler for any `Pong` messages coming back from the `Ponger` we'll build next:

<!-- snippet: sample_PongHandler -->
<a id='snippet-sample_ponghandler'></a>
```cs
using Messages;

namespace Pinger;

public class PongHandler
{
    public void Handle(Pong pong, ILogger<PongHandler> logger)
    {
        logger.LogInformation("Received Pong #{Number}", pong.Number);
    }
}
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Samples/PingPong/Pinger/PongHandler.cs#L1-L15' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_ponghandler' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

Okay then, next let's move on to building the `Ponger` application. This time I'll use `dotnet new console` to start the new
project, then add references to our *Messages* library and Wolverine itself. For the bootstrapping, add this code:

<!-- snippet: sample_PongerBootstrapping -->
<a id='snippet-sample_pongerbootstrapping'></a>
```cs
using Wolverine;
using Wolverine.Transports.Tcp;
using Microsoft.Extensions.Hosting;
using Oakton;

return await Host.CreateDefaultBuilder(args)
    .UseWolverine(opts =>
    {
        // Using Wolverine's built in TCP transport
        opts.ListenAtPort(5581);
    })
    .RunOaktonCommands(args);
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Samples/PingPong/Ponger/Program.cs#L1-L17' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_pongerbootstrapping' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

And a message handler for the `Ping` messages that will turn right around and shoot a `Pong` response right back
to the original sender:

<!-- snippet: sample_PingHandler -->
<a id='snippet-sample_pinghandler'></a>
```cs
using Wolverine;
using Messages;
using Microsoft.Extensions.Logging;

namespace Ponger;

public class PingHandler
{
    public ValueTask Handle(Ping ping, ILogger<PingHandler> logger, IMessageContext context)
    {
        logger.LogInformation("Got Ping #{Number}", ping.Number);
        return context.RespondToSenderAsync(new Pong { Number = ping.Number });
    }
}
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Samples/PingPong/Ponger/PingHandler.cs#L1-L19' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_pinghandler' title='Start of snippet'>anchor</a></sup>
<a id='snippet-sample_pinghandler-1'></a>
```cs
public static class PingHandler
{
    // Simple message handler for the PingMessage message type
    public static ValueTask Handle(
        // The first argument is assumed to be the message type
        PingMessage message,

        // Wolverine supports method injection similar to ASP.Net Core MVC
        // In this case though, IMessageContext is scoped to the message
        // being handled
        IMessageContext context)
    {
        ConsoleWriter.Write(ConsoleColor.Blue, $"Got ping #{message.Number}");

        var response = new PongMessage
        {
            Number = message.Number
        };

        // This usage will send the response message
        // back to the original sender. Wolverine uses message
        // headers to embed the reply address for exactly
        // this use case
        return context.RespondToSenderAsync(response);
    }
}
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Samples/PingPongWithRabbitMq/Ponger/PingHandler.cs#L8-L37' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_pinghandler-1' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

If I start up first the *Ponger* service, then the *Pinger* service, I'll see console output like this from *Pinger*:

```
info: Pinger.Worker[0]
      Sending Ping #11
info: Pinger.PongHandler[0]
      Received Pong #1
info: Wolverine.Runtime.WolverineRuntime[104]
      Successfully processed message Pong#01817277-f692-42d5-a3e4-35d9b7d119fb from tcp://localhost:5581/
info: Pinger.PongHandler[0]
      Received Pong #2
info: Wolverine.Runtime.WolverineRuntime[104]
      Successfully processed message Pong#01817277-f699-4340-a59d-9616aee61cb8 from tcp://localhost:5581/
info: Pinger.PongHandler[0]
      Received Pong #3
info: Wolverine.Runtime.WolverineRuntime[104]
      Successfully processed message Pong#01817277-f699-48ea-988b-9e835bc53020 from tcp://localhost:5581/
info: Pinger.PongHandler[0]
```

and output like this in the *Ponger* process:

```
info: Ponger.PingHandler[0]
      Got Ping #1
info: Wolverine.Runtime.WolverineRuntime[104]
      Successfully processed message Ping#01817277-d673-4357-84e3-834c36f3446c from tcp://localhost:5580/
info: Ponger.PingHandler[0]
      Got Ping #2
info: Wolverine.Runtime.WolverineRuntime[104]
      Successfully processed message Ping#01817277-da61-4c9d-b381-6cda92038d41 from tcp://localhost:5580/
info: Ponger.PingHandler[0]
      Got Ping #3
```


