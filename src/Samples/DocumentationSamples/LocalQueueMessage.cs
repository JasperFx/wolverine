using Microsoft.Extensions.Hosting;
using Wolverine;
using Wolverine.Attributes;

namespace DocumentationSamples;

#region sample_local_queue_routed_message

[LocalQueue("important")]
public class ImportanceMessage
{
}

#endregion

public static class LocalQueueConfiguration
{
    #region sample_disable_local_queue_routing

    public static async Task disable_queue_routing()
    {
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                // This will disable the conventional local queue
                // routing that would take precedence over other conventional
                // routing
                opts.Policies.DisableConventionalLocalRouting();
                
                // Other routing conventions. Rabbit MQ? SQS?
            }).StartAsync();

        #endregion
    }
}