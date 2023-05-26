using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Wolverine;

namespace DocumentationSamples;

public class LoggingUsage
{
    public static async Task show_entry_logging()
    {
        #region sample_log_message_starting

        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                // Opt into having Wolverine add a log message at the beginning
                // of the message execution
                opts.Policies.LogMessageStarting(LogLevel.Information);
            }).StartAsync();

        #endregion
    }
}