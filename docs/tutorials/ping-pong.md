# Ping/Pong Messaging with TCP

To show off some of the messaging, let's just build [a very simple "Ping/Pong" example](https://github.com/JasperFx/wolverine/tree/main/src/Samples/PingPong) that will exchange messages between two small .NET processes.

![Pinger and Ponger](/ping-pong.png)

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
using Messages;
using JasperFx;
using Pinger;
using Wolverine;
using Wolverine.Transports.Tcp;

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
    .RunJasperFxCommands(args);
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Samples/PingPong/Pinger/Program.cs#L1-L26' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_bootstrappingpinger' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

and the `Worker` class that's just going to publish a new `Ping` message once a second:

<!-- snippet: sample_PingPong_Worker -->
<a id='snippet-sample_pingpong_worker'></a>
```cs
using Messages;
using Wolverine;

namespace Pinger;

public class Worker : BackgroundService
{
    private readonly ILogger<Worker> _logger;
    private readonly IMessageBus _bus;

    public Worker(ILogger<Worker> logger, IMessageBus bus)
    {
        _logger = logger;
        _bus = bus;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var pingNumber = 1;

        while (!stoppingToken.IsCancellationRequested)
        {
            await Task.Delay(1000, stoppingToken);
            _logger.LogInformation("Sending Ping #{Number}", pingNumber);
            await _bus.PublishAsync(new Ping { Number = pingNumber });
            pingNumber++;
        }
    }
}
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Samples/PingPong/Pinger/Worker.cs#L1-L33' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_pingpong_worker' title='Start of snippet'>anchor</a></sup>
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
using Microsoft.Extensions.Hosting;
using JasperFx;
using Wolverine;
using Wolverine.Transports.Tcp;

return await Host.CreateDefaultBuilder(args)
    .UseWolverine(opts =>
    {
        // Using Wolverine's built in TCP transport
        opts.ListenAtPort(5581);
    })
    .RunJasperFxCommands(args);
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Samples/PingPong/Ponger/Program.cs#L1-L16' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_pongerbootstrapping' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

And a message handler for the `Ping` messages that will turn right around and shoot a `Pong` response right back
to the original sender:

<!-- snippet: sample_PingHandler -->
<a id='snippet-sample_pinghandler'></a>
```cs
using Messages;
using Microsoft.Extensions.Logging;
using Wolverine;

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
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Samples/PingPong/Ponger/PingHandler.cs#L1-L18' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_pinghandler' title='Start of snippet'>anchor</a></sup>
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
        AnsiConsole.MarkupLine($"[blue]Got ping #{message.Number}[/]");

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
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Samples/PingPongWithRabbitMq/Ponger/PingHandler.cs#L6-L35' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_pinghandler-1' title='Start of snippet'>anchor</a></sup>
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


