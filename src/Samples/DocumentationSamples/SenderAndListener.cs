using Microsoft.Extensions.Hosting;
using Wolverine;

namespace DocumentationSamples;

public static class SenderAndListener
{
    public static async Task sample()
    {
        #region sample_SenderAndListener

        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                // All messages get published via TCP
                // to port 5555 on the local box
                opts.PublishAllMessages()
                    .To("tcp://localhost:5555");


                // Listen for incoming messages at
                // port 6666
                opts.ListenForMessagesFrom("tcp://localhost:6666");
            }).StartAsync();

        #endregion
    }
}