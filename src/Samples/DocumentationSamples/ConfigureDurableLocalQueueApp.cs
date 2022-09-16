using Wolverine;
using Microsoft.Extensions.Hosting;

namespace DocumentationSamples
{
    internal static class DurableScheduleMessagesSample
    {
        public static async Task Configuration()
        {
            #region sample_DurableScheduledMessagesLocalQueue

            using var host = await Host.CreateDefaultBuilder()
                .UseWolverine(opts =>
                {
                    opts.DurableScheduledMessagesLocalQueue

                        // Allow no more than 3 scheduled messages
                        // to execute at one time
                        .MaximumParallelMessages(3);
                }).StartAsync();

            #endregion
        }


        public static async Task ConfigureDefaultQueue()
        {
            #region sample_ConfigureDefaultQueue

            using var host = await Host.CreateDefaultBuilder()
                .UseWolverine(opts =>
                {
                    opts.DefaultLocalQueue.MaximumParallelMessages(3);
                }).StartAsync();

            #endregion
        }
    }

}
