using Microsoft.Extensions.Hosting;
using Wolverine.Tracking;
using Xunit;
using Xunit.Abstractions;

namespace Wolverine.AzureServiceBus.Tests;

public class clean_off_queues
{
    private readonly ITestOutputHelper _output;

    public clean_off_queues(ITestOutputHelper output)
    {
        _output = output;
    }

    //[Fact] -- leaving this here for later
    public async Task clean_off_existing_queues()
    {
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.UseAzureServiceBusTesting();
            }).StartAsync();

        var transport = host.GetRuntime().Options.Transports.GetOrCreate<AzureServiceBusTransport>();


        while (true)
        {
            var queueNames = new List<string>();
            await foreach (var what in transport.ManagementClient.GetQueuesAsync(CancellationToken.None))
            {
                queueNames.Add(what.Name);
            }

            if (!queueNames.Any())
            {
                return;
            }

            foreach (var queueName in queueNames)
            {
                await transport.ManagementClient.DeleteQueueAsync(queueName, CancellationToken.None);
                _output.WriteLine("Deleted " + queueName);
            }
        }
        
        
        
    }
}