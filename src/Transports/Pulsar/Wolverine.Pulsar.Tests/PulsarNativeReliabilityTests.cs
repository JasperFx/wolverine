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

[Collection("pulsar")]
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

                opts.UsePulsar(b =>
                {
                    b.ServiceUrl(new Uri("pulsar://127.0.0.1:6650"));
                });

                opts.IncludeType<SRMessageHandlers>();

                opts.PublishMessage<SRMessage1>()
                    .ToPulsarTopic(topicPath);

                opts.ListenToPulsarTopic(topicPath)
                    .WithSharedSubscriptionType()
                    .DeadLetterQueueing(DeadLetterTopic.DefaultNative)
                    .RetryLetterQueueing(new RetryLetterTopic([TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(3)]))
                    //.ProcessInline();
                    .BufferedInMemory();


                var topicPath2 = $"persistent://public/default/no-retry-{topic}";
                opts.IncludeType<SRMessageHandlers>();

                opts.PublishMessage<SRMessage2>()
                    .ToPulsarTopic(topicPath2);

                opts.ListenToPulsarTopic(topicPath2)
                    .WithSharedSubscriptionType()
                    .DeadLetterQueueing(DeadLetterTopic.DefaultNative)
                    .DisableRetryLetterQueueing()
                    .ProcessInline();

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
        var session =  await WolverineHost.TrackActivity(TimeSpan.FromSeconds(1000))
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
            .ShouldBe(4);

        session.Requeued
            .MessagesOf<SRMessage1>()
            .Count()
            .ShouldBe(3);


        // TODO: I Guess the capture of the envelope headers occurs before we manipulate it
        var firstRequeuedEnvelope = session.Requeued.Envelopes().First();
        firstRequeuedEnvelope.ShouldSatisfyAllConditions(
            () => firstRequeuedEnvelope.Attempts.ShouldBe(1),
            () => firstRequeuedEnvelope.Headers.ContainsKey("DELAY_TIME").ShouldBeFalse()
        );
        var secondRequeuedEnvelope = session.Requeued.Envelopes().Skip(1).First();
        secondRequeuedEnvelope.ShouldSatisfyAllConditions(
            () => secondRequeuedEnvelope.Attempts.ShouldBe(2),
            () => secondRequeuedEnvelope.Headers.ContainsKey("DELAY_TIME").ShouldBeTrue(),
            () => secondRequeuedEnvelope.Headers["DELAY_TIME"].ShouldBe(TimeSpan.FromSeconds(4).TotalMilliseconds.ToString())
        );

        var thirdRequeuedEnvelope = session.Requeued.Envelopes().Skip(2).First();
        thirdRequeuedEnvelope.ShouldSatisfyAllConditions(
            () => thirdRequeuedEnvelope.Attempts.ShouldBe(3),
            () => thirdRequeuedEnvelope.Headers.ContainsKey("DELAY_TIME").ShouldBeTrue(),
            () => thirdRequeuedEnvelope.Headers["DELAY_TIME"].ShouldBe(TimeSpan.FromSeconds(2).TotalMilliseconds.ToString())
        );


        var dlqEnvelope = session.MovedToErrorQueue.Envelopes().First();
        dlqEnvelope.ShouldSatisfyAllConditions(
            () => dlqEnvelope.Headers.ContainsKey(PulsarEnvelopeConstants.Exception).ShouldBeTrue(),
            () => dlqEnvelope.Headers[PulsarEnvelopeConstants.ReconsumeTimes].ShouldBe("3")
        );

    }

    [Fact]
    public async Task run_setup_with_simulated_exception_in_handler_only_native_dead_lettered_queue()
    {
        var session = await WolverineHost.TrackActivity(TimeSpan.FromSeconds(100))
            .DoNotAssertOnExceptionsDetected()
            .IncludeExternalTransports()
            .WaitForCondition(new WaitForDeadLetteredMessage<SRMessage2>())
            .SendMessageAndWaitAsync(new SRMessage2());


        session.Sent.AllMessages();
        session.MovedToErrorQueue
            .MessagesOf<SRMessage2>()
            .Count()
            .ShouldBe(1);

        session.Received
            .MessagesOf<SRMessage2>()
            .Count()
            .ShouldBe(1);

        session.Requeued
            .MessagesOf<SRMessage2>()
            .Count()
            .ShouldBe(0);




        var firstEnvelope = session.MovedToErrorQueue.Envelopes().First();
        firstEnvelope.ShouldSatisfyAllConditions(
            () => firstEnvelope.Headers.ContainsKey(PulsarEnvelopeConstants.Exception).ShouldBeTrue(),
            () => firstEnvelope.Headers.ContainsKey(PulsarEnvelopeConstants.ReconsumeTimes).ShouldBeFalse()
        );

    }

    [Fact]
    public async Task verify_retry_delay_intervals_are_respected()
    {
        // custom retry intervals: [4s, 2s, 3s]
        //
        // IMPORTANT: The DELAY_TIME header in tracked envelopes reflects the delay from the PREVIOUS retry,
        // not the current one being sent. This is because:
        // BuildMessageMetadata adds DELAY_TIME to the outgoing MessageMetadata (Pulsar message properties)
        // writeIncomingHeaders copies Pulsar properties into envelope.Headers when RECEIVING a message
        // Wolverine tracking captures the envelope at requeue time, which has headers from the previous receive
        //
        // Flow with delays [4s, 2s, 3s]:
        // - Requeue #1 (Attempts=1): No DELAY_TIME in headers (message came from main topic)
        //                            => Sends with DELAY_TIME=4000ms
        // - Requeue #2 (Attempts=2): DELAY_TIME=4000ms in headers (from previous retry queue receive)
        //                            => Sends with DELAY_TIME=2000ms
        // - Requeue #3 (Attempts=3): DELAY_TIME=2000ms in headers (from previous retry queue receive)
        //                            => Sends with DELAY_TIME=3000ms
        // - DLQ (Attempts=4):        DELAY_TIME=3000ms in headers (from previous retry queue receive)
        //
        // Therefore, we verify delays by checking:
        // - Requeue envelopes [1] and [2] for the first two configured delays (4s, 2s)
        // - DLQ envelope for the third configured delay (3s)

        var session = await WolverineHost.TrackActivity(TimeSpan.FromSeconds(100))
            .DoNotAssertOnExceptionsDetected()
            .IncludeExternalTransports()
            .WaitForCondition(new WaitForDeadLetteredMessage<SRMessage1>())
            .SendMessageAndWaitAsync(new SRMessage1());

        var requeuedEnvelopes = session.Requeued.Envelopes().ToList();
        requeuedEnvelopes.Count.ShouldBe(3);

        // First requeue: no DELAY_TIME header (message originated from main topic, not retry queue)
        requeuedEnvelopes[0].Headers.ContainsKey(PulsarEnvelopeConstants.DelayTimeMetadataKey).ShouldBeFalse();

        // Second requeue: DELAY_TIME reflects the FIRST configured delay (4s) from previous retry
        requeuedEnvelopes[1].Headers[PulsarEnvelopeConstants.DelayTimeMetadataKey]
            .ShouldBe(TimeSpan.FromSeconds(4).TotalMilliseconds.ToString());

        // Third requeue: DELAY_TIME reflects the SECOND configured delay (2s) from previous retry
        requeuedEnvelopes[2].Headers[PulsarEnvelopeConstants.DelayTimeMetadataKey]
            .ShouldBe(TimeSpan.FromSeconds(2).TotalMilliseconds.ToString());

        // DLQ envelope: DELAY_TIME reflects the THIRD configured delay (3s) from previous retry
        var dlqEnvelope = session.MovedToErrorQueue.Envelopes().First();
        dlqEnvelope.Headers[PulsarEnvelopeConstants.DelayTimeMetadataKey]
            .ShouldBe(TimeSpan.FromSeconds(3).TotalMilliseconds.ToString());
    }

    [Fact]
    public async Task verify_message_attempts_increment_correctly()
    {
        var session = await WolverineHost.TrackActivity(TimeSpan.FromSeconds(100))
            .DoNotAssertOnExceptionsDetected()
            .IncludeExternalTransports()
            .WaitForCondition(new WaitForDeadLetteredMessage<SRMessage1>())
            .SendMessageAndWaitAsync(new SRMessage1());

        // 4 total receives: initial + 3 retries
        session.Received.MessagesOf<SRMessage1>().Count().ShouldBe(4);

        var requeuedEnvelopes = session.Requeued.Envelopes().ToList();
        requeuedEnvelopes[0].Attempts.ShouldBe(1);
        requeuedEnvelopes[1].Attempts.ShouldBe(2);
        requeuedEnvelopes[2].Attempts.ShouldBe(3);

        // Final DLQ message should have reconsume times = 3 (number of retries)
        var dlqEnvelope = session.MovedToErrorQueue.Envelopes().First();
        dlqEnvelope.Headers[PulsarEnvelopeConstants.ReconsumeTimes].ShouldBe("3");
    }



    public async Task DisposeAsync()
    {
        await WolverineHost.StopAsync();
        WolverineHost.Dispose();
    }


}

public class SRMessage1;
public class SRMessage2;


public class SRMessageHandlers
{
    public Task Handle(SRMessage1 message, IMessageContext context)
    {
        throw new InvalidOperationException("Simulated exception");
    }

    public Task Handle(SRMessage2 message, IMessageContext context)
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
        {
            _found = true;
        }
    }

    public bool IsCompleted() => _found;
}

