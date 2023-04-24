using Microsoft.Extensions.Hosting;
using Wolverine.Transports.Tcp;

namespace Wolverine.MessagePack.Tests;

public class Samples
{
    public async Task bootstrap()
    {
        #region sample_using_messagepack_for_the_default_for_the_app

        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                // Make MessagePack the default serializer throughout this application
                opts.UseMessagePackSerialization();
            }).StartAsync();

        #endregion
    }

    public async Task bootstrap_selectively()
    {
        #region sample_using_messagepack_on_selected_endpoints

        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                // Use MessagePack on a local queue
                opts.LocalQueue("one").UseMessagePackSerialization();

                // Use MessagePack on a listening endpoint
                opts.ListenAtPort(2223).UseMessagePackSerialization();

                // Use MessagePack on one subscriber
                opts.PublishAllMessages().ToPort(2222).UseMessagePackSerialization();
            }).StartAsync();

        #endregion
    }
}
