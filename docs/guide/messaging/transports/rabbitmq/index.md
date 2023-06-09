# Using Rabbit MQ

::: tip
Wolverine uses the [Rabbit MQ .NET Client](https://www.rabbitmq.com/dotnet.html) to connect to Rabbit MQ.
:::

## Installing
~~~~
All the code samples in this section are from the [Ping/Pong with Rabbit MQ sample project](https://github.com/JasperFx/wolverine/tree/main/src/Samples/PingPongWithRabbitMq).

To use [RabbitMQ](http://www.rabbitmq.com/) as a transport with Wolverine, first install the `Wolverine.RabbitMQ` library via nuget to your project. Behind the scenes, this package uses the [RabbitMQ C# Client](https://www.rabbitmq.com/dotnet.html) to both send and receive messages from RabbitMQ.

<!-- snippet: sample_bootstrapping_rabbitmq -->
<a id='snippet-sample_bootstrapping_rabbitmq'></a>
```cs
return await Host.CreateDefaultBuilder(args)
    .UseWolverine(opts =>
    {
        // Listen for messages coming into the pongs queue
        opts
            .ListenToRabbitQueue("pongs")

            // This won't be necessary by the time Wolverine goes 2.0
            // but for now, I've got to help Wolverine out a little bit
            .UseForReplies();

        // Publish messages to the pings queue
        opts.PublishMessage<PingMessage>().ToRabbitExchange("pings");

        // Configure Rabbit MQ connection properties programmatically
        // against a ConnectionFactory
        opts.UseRabbitMq(rabbit =>
            {
                // Using a local installation of Rabbit MQ
                // via a running Docker image
                rabbit.HostName = "localhost";
            })
            // Directs Wolverine to build any declared queues, exchanges, or
            // bindings with the Rabbit MQ broker as part of bootstrapping time
            .AutoProvision();

        // Or you can use this functionality to set up *all* known
        // Wolverine (or Marten) related resources on application startup
        opts.Services.AddResourceSetupOnStartup();

        // This will send ping messages on a continuous
        // loop
        opts.Services.AddHostedService<PingerService>();
    }).RunOaktonCommands(args);
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Samples/PingPongWithRabbitMq/Pinger/Program.cs#L7-L44' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_bootstrapping_rabbitmq' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

See the [Rabbit MQ .NET Client documentation](https://www.rabbitmq.com/dotnet-api-guide.html#connecting) for more information about configuring the `ConnectionFactory` to connect to Rabbit MQ.





