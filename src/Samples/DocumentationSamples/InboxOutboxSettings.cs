using JasperFx.Core;
using Microsoft.Extensions.Hosting;
using Wolverine;

namespace DocumentationSamples;

public class InboxOutboxSettings
{
    public async Task configure()
    {
        #region sample_using_inbox_outbox_stale_time

        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                // configure the actual message persistence...

                // This directs Wolverine to "bump" any messages marked
                // as being owned by a specific node but older than
                // these thresholds as  being open to any node pulling 
                // them in
                
                // TL;DR: make Wolverine go grab stale messages and make
                // sure they are processed or sent to the messaging brokers
                opts.Durability.InboxStaleTime = 5.Minutes();
                opts.Durability.OutboxStaleTime = 5.Minutes();
            }).StartAsync();

        #endregion
    }
}