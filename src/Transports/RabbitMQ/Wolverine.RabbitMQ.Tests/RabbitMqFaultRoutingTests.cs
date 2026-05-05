using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Wolverine.ComplianceTests.Compliance;
using Wolverine.ComplianceTests.ErrorHandling.Faults;
using Wolverine.ErrorHandling;

namespace Wolverine.RabbitMQ.Tests;

public class RabbitMqFaultRoutingTests : TransportFaultRoutingCompliance
{
    // Per-test-suite queue name to avoid collisions with parallel runs and
    // any stale broker state left over from previous executions.
    private static readonly string FaultQueue = $"fault-compliance-{Guid.NewGuid():n}";

    public override async Task<IHost> BuildSenderAsync()
    {
        return await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.UseRabbitMq().AutoProvision();

                opts.PublishMessage<Fault<OrderPlaced>>().ToRabbitQueue(FaultQueue);

                opts.OnException<Exception>().MoveToErrorQueue();
                opts.PublishFaultEvents();

                opts.Discovery.IncludeType<AlwaysFailsHandler>();
            }).StartAsync();
    }

    public override async Task<IHost> BuildReceiverAsync(FaultSink sink)
    {
        return await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.UseRabbitMq().AutoProvision();

                opts.ListenToRabbitQueue(FaultQueue);

                opts.Services.AddSingleton(sink);
                opts.Discovery.IncludeType<FaultSinkHandler>();
            }).StartAsync();
    }
}
