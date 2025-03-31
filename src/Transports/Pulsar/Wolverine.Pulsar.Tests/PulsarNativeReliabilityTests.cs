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
                    //.ProcessInline();
                    .BufferedInMemory();

                //opts.ListenToPulsarTopic(topicPath + "-DLQ")
                //    .WithSharedSubscriptionType()
                //    .ProcessInline();


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
        var session =  await WolverineHost.TrackActivity(TimeSpan.FromSeconds(100))
            //.WaitForMessageToBeReceivedAt<SRMessage1>(WolverineHost)
            .DoNotAssertOnExceptionsDetected()
            .IncludeExternalTransports()
            .WaitForCondition(new WaitForDeadLetteredMessage<SRMessage1>())
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

        session.MovedToRetryQueue
            .MessagesOf<SRMessage1>()
            .Count()
            .ShouldBe(2);

        // TODO: I Guess the capture of the envelope headers occurs before we manipulate it
        //var firstRequeuedEnvelope = session.MovedToRetryQueue.Envelopes().First();
        //firstRequeuedEnvelope.ShouldSatisfyAllConditions(
        //    () => firstRequeuedEnvelope.Headers.ContainsKey("DELAY_TIME").ShouldBeTrue(),
        //    () => firstRequeuedEnvelope.Headers["DELAY_TIME"].ShouldBe(TimeSpan.FromSeconds(1).TotalMilliseconds.ToString())
        //);
        //var secondRequeuedEnvelope = session.MovedToRetryQueue.Envelopes().Skip(1).First();
        //secondRequeuedEnvelope.ShouldSatisfyAllConditions(
        //    () => secondRequeuedEnvelope.Headers.ContainsKey("DELAY_TIME").ShouldBeTrue(),
        //    () => secondRequeuedEnvelope.Headers["DELAY_TIME"].ShouldBe(TimeSpan.FromSeconds(2).TotalMilliseconds.ToString())
        //);


        var firstEnvelope = session.MovedToErrorQueue.Envelopes().First();
        firstEnvelope.ShouldSatisfyAllConditions(
            () => firstEnvelope.Headers.ContainsKey(PulsarEnvelopeConstants.Exception).ShouldBeTrue(),
            () => firstEnvelope.Headers[PulsarEnvelopeConstants.ReconsumeTimes].ShouldBe("2"),
            () => firstEnvelope.Headers["DELAY_TIME"].ShouldBe(TimeSpan.FromSeconds(2).TotalMilliseconds.ToString())
        );

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
    public Task Handle(SRMessage1 message, IMessageContext context)
    {
        throw new InvalidOperationException("Simulated exception");
    }

}



public class WaitForDeadLetteredMessage<T> : ITrackedCondition
{

    private bool _found;

    public WaitForDeadLetteredMessage()
    {

    }

    public void Record(EnvelopeRecord record)
    {
        if (record.Envelope.Message is T && record.MessageEventType == MessageEventType.MovedToErrorQueue )
           // && record.Envelope.Destination?.ToString().Contains(_dlqTopic) == true)
        {
            _found = true;
        }
    }

    public bool IsCompleted() => _found;
}

