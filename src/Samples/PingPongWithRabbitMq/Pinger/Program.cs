using Oakton;
using Oakton.Resources;
using Pinger;
using Wolverine;
using Wolverine.RabbitMQ;

#region sample_bootstrapping_rabbitmq

return await Host.CreateDefaultBuilder(args)
    .UseWolverine(opts =>
    {
        // Listen for messages coming into the pongs queue
        opts
            .ListenToRabbitQueue("pongs");

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

#endregion
