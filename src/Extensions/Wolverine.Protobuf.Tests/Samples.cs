using Microsoft.Extensions.Hosting;
using Wolverine.Transports.Tcp;

namespace Wolverine.Protobuf.Tests;

public class Samples
{
    public async Task bootstrap()
    {
        #region sample_using_protobuf_for_the_default_for_the_app

        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                // Make Protobuf the default serializer throughout this application
                opts.UseProtobufSerialization();
            }).StartAsync();

        #endregion
    }

    public async Task bootstrap_selectively()
    {
        #region sample_using_protobuf_on_selected_endpoints

        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                // Use Protobuf on a local queue
                opts.LocalQueue("one").UseProtobufSerialization();

                // Use Protobuf on a listening endpoint
                opts.ListenAtPort(2223).UseProtobufSerialization();

                // Use Protobuf on one subscriber
                opts.PublishAllMessages().ToPort(2222).UseProtobufSerialization();
            }).StartAsync();

        #endregion
    }
}