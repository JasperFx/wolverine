using Microsoft.Extensions.Hosting;
using Wolverine.Transports.Tcp;

namespace Wolverine.MemoryPack.Tests;

public class Samples
{
    public async Task bootstrap()
    {
        #region sample_using_memorypack_for_the_default_for_the_app

        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                // Make MemoryPack the default serializer throughout this application
                opts.UseMemoryPackSerialization();
            }).StartAsync();

        #endregion
    }

    public async Task bootstrap_selectively()
    {
        #region sample_using_memorypack_on_selected_endpoints

        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                // Use MemoryPack on a local queue
                opts.LocalQueue("one").UseMemoryPackSerialization();

                // Use MemoryPack on a listening endpoint
                opts.ListenAtPort(2223).UseMemoryPackSerialization();

                // Use MemoryPack on one subscriber
                opts.PublishAllMessages().ToPort(2222).UseMemoryPackSerialization();
            }).StartAsync();

        #endregion
    }
}