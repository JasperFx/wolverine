using JasperFx.Core.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine.AmazonSqs.Internal;
using Wolverine.Runtime;

namespace Wolverine.AmazonSqs.Tests.Bugs;

public class disabling_dead_letter_queue
{
    [Fact]
    public async Task do_not_create_dead_letter_queue()
    {
        #region sample_disabling_all_sqs_dead_letter_queueing

        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.UseAmazonSqsTransportLocally()
                    // Disable all native SQS dead letter queueing
                    .DisableAllNativeDeadLetterQueues()
                    .AutoProvision();

                opts.ListenToSqsQueue("incoming");
            }).StartAsync();

        #endregion

        // This is fugly
        var transport = host.Services.GetRequiredService<IWolverineRuntime>().As<WolverineRuntime>()
            .Options.Transports.GetOrCreate<AmazonSqsTransport>();
        
        transport.Queues.Contains(AmazonSqsTransport.DeadLetterQueueName)
            .ShouldBeFalse();
    }
}