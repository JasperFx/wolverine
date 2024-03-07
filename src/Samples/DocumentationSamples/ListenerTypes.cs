using Microsoft.Extensions.Hosting;
using Wolverine;
using Wolverine.Postgresql;
using Wolverine.RabbitMQ;
using Wolverine.Transports;

namespace DocumentationSamples;

public static class ListenerTypes
{
    public static async Task configure_listeners()
    {
        #region sample_configuring_listener_types

        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                // The Rabbit MQ transport supports all three types of listeners
                opts.UseRabbitMq();
                
                // The durable mode requires some sort of envelope storage
                opts.PersistMessagesWithPostgresql("some connection string");

                opts.ListenToRabbitQueue("inline")
                    // Process inline, default is with one listener
                    .ProcessInline()
                    
                    // But, you can use multiple, parallel listeners
                    .ListenerCount(5);
                
                opts.ListenToRabbitQueue("buffered")
                    // Buffer the messages in memory for increased throughput
                    .BufferedInMemory(new BufferingLimits(1000, 500));

                opts.ListenToRabbitQueue("durable")
                    // Opt into durable inbox mechanics
                    .UseDurableInbox(new BufferingLimits(1000, 500));

            }).StartAsync();

        #endregion
    }
}