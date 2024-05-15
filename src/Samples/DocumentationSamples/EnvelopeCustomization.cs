using JasperFx.Core;
using Microsoft.Extensions.Hosting;
using Wolverine;
using Wolverine.Transports.Tcp;

namespace DocumentationSamples;

public static class EnvelopeCustomizationSamples
{
    public static async Task monitoring_data_publisher()
    {
        #region sample_MonitoringDataPublisher

        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.PublishAllMessages()
                    .ToPort(2222)

                    // Set a message expiration on all
                    // outgoing messages to this
                    // endpoint
                    .CustomizeOutgoing(env => env.DeliverWithin = 2.Seconds());
            }).StartAsync();

        #endregion
    }
}