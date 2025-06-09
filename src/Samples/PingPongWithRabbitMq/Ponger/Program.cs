using JasperFx.Core;
using JasperFx;
using Wolverine;
using Wolverine.RabbitMQ;

return await Host.CreateDefaultBuilder(args)
    .UseWolverine(opts =>
    {
        opts.ApplicationAssembly = typeof(Program).Assembly;
        
        // Going to listen to a queue named "pings", but disregard any messages older than
        // 15 seconds
        opts.ListenToRabbitQueue("pings", queue => queue.TimeToLive(15.Seconds()));

        // Configure Rabbit MQ connection to the connection string
        // named "rabbit" from IConfiguration. This is *a* way to use
        // Wolverine + Rabbit MQ using Aspire
        opts.UseRabbitMqUsingNamedConnection("rabbit")
            .DeclareExchange("pings", exchange =>
            {
                // Also declares the queue too
                exchange.BindQueue("pings");
            })
            .AutoProvision()

            // Option to blow away existing messages in
            // all queues on application startup
            .AutoPurgeOnStartup();
    })
    .RunJasperFxCommands(args);