using JasperFx.Core;
using Microsoft.Extensions.Hosting;
using Oakton;
using Shouldly;
using Wolverine.ComplianceTests;
using Wolverine.ComplianceTests.Compliance;
using Wolverine.ComplianceTests.Scheduling;
using Wolverine.Tracking;
using Xunit;

namespace Wolverine.Pulsar.Tests;

public class PulsarNativeReliabilityTests : /*TransportComplianceFixture,*/ IAsyncLifetime
{
    public IHost WolverineHost;

    public PulsarNativeReliabilityTests()
    {
    }

    private IHostBuilder ConfigureBuilder()
    {
        return Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {

                var topic = Guid.NewGuid().ToString();
                var topicPath = $"persistent://public/default/compliance{topic}";

                opts.UsePulsar(b => { });

                opts.IncludeType<SRMessageHandlers>();

                opts.PublishMessage<SRMessage1>()
                    .ToPulsarTopic(topicPath);

                opts.ListenToPulsarTopic(topicPath)
                    .WithSharedSubscriptionType()
                    .RetryLetterQueueing(new RetryLetterTopic([TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2)]))
                    .DeadLetterQueueing(DeadLetterTopic.DefaultNative)
                    .ProcessInline();
                //.BufferedInMemory();


            });
    }

    public async Task InitializeAsync()
    {
        WolverineHost = ConfigureBuilder().Build();
        await WolverineHost.StartAsync();
    }

    [Fact]
    public async Task run_setup_with_simulated_exception_in_handler()
    {
        var session =  await WolverineHost.TrackActivity(TimeSpan.FromSeconds(10))
            .WaitForMessageToBeReceivedAt<SRMessage1>(WolverineHost)
            .DoNotAssertOnExceptionsDetected()
            .IncludeExternalTransports()
            .SendMessageAndWaitAsync(new SRMessage1());


        session.Sent.AllMessages();
        session.MovedToErrorQueue
            .MessagesOf<SRMessage1>()
            .Count()
            .ShouldBe(1);

        session.Received
            .MessagesOf<SRMessage1>()
            .Count()
            .ShouldBe(3);

        session.Requeued
            .MessagesOf<SRMessage1>()
            .Count()
            .ShouldBe(2);
    }

   

    public async Task DisposeAsync()
    {
        await WolverineHost.StopAsync();
        WolverineHost.Dispose();
    }


}

public class SRMessage1;


public class SRMessageHandlers
{
    public Task Handle(SRMessage1 message)
    {
        throw new InvalidOperationException("Simulated exception");
    }

}
