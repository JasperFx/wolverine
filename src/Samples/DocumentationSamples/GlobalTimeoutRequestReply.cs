using JasperFx.Core;
using Microsoft.Extensions.Hosting;
using Wolverine;

namespace DocumentationSamples;

public class GlobalTimeoutRequestReply
{
    public static async Task global_timeout()
    {
        #region sample_global_timeout_for_remote_invocation

        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                // Set a global default timeout for remote
                // invocation or request/reply operations
                opts.DefaultRemoteInvocationTimeout = 10.Seconds();
            }).StartAsync();

        #endregion
    }
}