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

    public static async Task customizing_log_levels()
    {
        #region sample_turning_down_message_logging

        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                // Turn off all logging of the message execution starting and finishing
                // The default is Debug
                opts.Policies.MessageExecutionLogLevel(LogLevel.None);


                // Turn down Wolverine's built in logging of all successful
                // message processing
                opts.Policies.MessageSuccessLogLevel(LogLevel.Debug);
            }).StartAsync();

        #endregion
    }
}

