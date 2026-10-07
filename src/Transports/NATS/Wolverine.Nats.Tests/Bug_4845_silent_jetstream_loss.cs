using IntegrationTests;
using JasperFx.Core;
using Microsoft.Extensions.Hosting;
using NATS.Client.Core;
using NATS.Client.JetStream;
using NATS.Client.JetStream.Models;
using NATS.Net;
using Shouldly;
using Wolverine.Attributes;
using Wolverine.ErrorHandling;
using Xunit;

namespace Wolverine.Nats.Tests;

/// <summary>
/// GH-4845 reproductions. A test whose loss is fixed asserts the fixed behavior. The others assert the
/// CURRENT (buggy) behavior so that the loss is on the record; the comment above each one says what it
/// should assert once fixed.
/// </summary>
[Collection("NATS Integration")]
[Trait("Category", "Integration")]
public class Bug_4845_silent_jetstream_loss
{
    private readonly ITestOutputHelper _output;

    public Bug_4845_silent_jetstream_loss(ITestOutputHelper output) => _output = output;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>
    /// GH-4845 #1. A JetStream publish the server REFUSES comes back as a PubAck with <c>Error</c> set,
    /// not as an exception. <c>JetStreamPublisher</c> only read <c>ack.Seq</c>, so the send reported
    /// success and the durable outbox deleted a message the stream never stored.
    ///
    /// Forced here with MaxMsgs=1 + DiscardPolicy.New: the second publish is refused, and that failure
    /// has to reach Wolverine's sending failure handling. SendAsync itself does not throw for an inline
    /// endpoint: the inline sending agent hands a failed send to the endpoint's sending failure policies
    /// and otherwise to its retry block, so the test watches the policy.
    /// </summary>
    [Fact]
    public async Task rejected_jetstream_publish_reaches_the_sending_failure_policies()
    {
        var natsUrl = NatsTestHelpers.ResolveUrl();
        if (!await NatsTestHelpers.IsNatsAvailable(natsUrl)) return;

        var stream = $"REJECT_{Guid.NewGuid():N}";
        var subject = $"reject.full.{Guid.NewGuid():N}";
        var sendFailure = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);

        using var host = await Host.CreateDefaultBuilder()
            .ConfigureLogging(l => l.AddXunitLogging(_output))
            .UseWolverine(opts =>
            {
                opts.ServiceName = "RejectedPublish";
                opts.UseNats(natsUrl)
                    .AutoProvision()
                    .DefineStream(stream, s =>
                    {
                        s.WithSubjects(subject);
                        // A full stream that refuses new messages instead of evicting old ones. This is
                        // the DiscardPolicy.New case from the issue.
                        s.MaxMessages = 1;
                        s.DiscardPolicy = StreamConfigDiscard.New;
                    });

                opts.Policies.DisableConventionalLocalRouting();
                opts.PublishMessage<OrderPlaced>().ToNatsSubject(subject).UseJetStream(stream).SendInline()
                    .ConfigureSending(sending => sending.OnException<Exception>().CustomAction((_, _, e) =>
                    {
                        sendFailure.TrySetResult(e);
                        return ValueTask.CompletedTask;
                    }, "Record the failed send"));
            })
            .StartAsync(cancellationToken: Ct);

        var bus = host.MessageBus();

        // First send fills the stream to its one-message limit.
        await bus.SendAsync(new OrderPlaced(Guid.NewGuid().ToString("N")));

        // The server refuses this one, and the send has to fail.
        await bus.SendAsync(new OrderPlaced(Guid.NewGuid().ToString("N")));

        var exception = (await sendFailure.Task.WaitAsync(10.Seconds(), Ct)).ShouldBeOfType<NatsJSApiException>();
        _output.WriteLine($"refused: {exception.Error.Description} ({exception.Error.ErrCode})");

        (await CountStreamMessagesAsync(natsUrl, stream)).ShouldBe(1);
    }

    /// <summary>
    /// GH-4845 #1, control: prove the server really does answer a refused publish with an error PubAck
    /// rather than an exception, so the fix (inspect <c>ack.Error</c>) is the right one and
    /// <c>EnsureSuccess()</c> is not (it would also fail a benign duplicate).
    /// </summary>
    [Fact]
    public async Task nats_answers_a_refused_publish_with_an_error_puback_not_an_exception()
    {
        var natsUrl = NatsTestHelpers.ResolveUrl();
        if (!await NatsTestHelpers.IsNatsAvailable(natsUrl)) return;

        var streamName = $"REJECTRAW_{Guid.NewGuid():N}";
        var subject = $"reject.raw.{Guid.NewGuid():N}";

        await using var connection = new NatsConnection(new NatsOpts { Url = natsUrl });
        await connection.ConnectAsync();
        var js = connection.CreateJetStreamContext();

        await js.CreateStreamAsync(new StreamConfig(streamName, [subject])
        {
            MaxMsgs = 1,
            Discard = StreamConfigDiscard.New,
            DuplicateWindow = 2.Minutes()
        }, Ct);

        var first = await js.PublishAsync(subject, new byte[] { 1 },
            opts: new NatsJSPubOpts { MsgId = "one" }, cancellationToken: Ct);
        first.Error.ShouldBeNull();
        first.Seq.ShouldBe(1ul);

        // Refused: the stream is full and will not evict.
        var refused = await js.PublishAsync(subject, new byte[] { 2 },
            opts: new NatsJSPubOpts { MsgId = "two" }, cancellationToken: Ct);

        _output.WriteLine($"refused: Seq={refused.Seq}, Duplicate={refused.Duplicate}, Error={refused.Error?.Description} ({refused.Error?.ErrCode})");

        refused.Error.ShouldNotBeNull("NATS answered a refused publish with a clean PubAck -- the premise of GH-4845 #1 would be wrong");

        // ...and a duplicate is a clean, errorless ack that must stay a success.
        var duplicate = await js.PublishAsync(subject, new byte[] { 1 },
            opts: new NatsJSPubOpts { MsgId = "one" }, cancellationToken: Ct);
        _output.WriteLine($"duplicate: Seq={duplicate.Seq}, Duplicate={duplicate.Duplicate}, Error={duplicate.Error?.Description}");
        duplicate.Duplicate.ShouldBeTrue();
        duplicate.Error.ShouldBeNull();
    }

    /// <summary>
    /// GH-4845 #3. <c>UseShardedNatsSubjects()</c> declares its shard streams with <c>AsWorkQueue()</c>,
    /// which sets <c>Retention = Interest</c>. Interest retention drops a message published while no
    /// consumer is bound -- exactly the startup/rebalance window the sharded topology has. Real work-queue
    /// retention holds the message until a consumer acks it.
    /// </summary>
    [Fact]
    public async Task interest_retention_drops_a_message_published_with_no_bound_consumer()
    {
        var natsUrl = NatsTestHelpers.ResolveUrl();
        if (!await NatsTestHelpers.IsNatsAvailable(natsUrl)) return;

        var interestStream = $"INTEREST_{Guid.NewGuid():N}";
        var workQueueStream = $"WORKQ_{Guid.NewGuid():N}";
        var interestSubject = $"retention.interest.{Guid.NewGuid():N}";
        var workQueueSubject = $"retention.workq.{Guid.NewGuid():N}";

        await using var connection = new NatsConnection(new NatsOpts { Url = natsUrl });
        await connection.ConnectAsync();
        var js = connection.CreateJetStreamContext();

        // What AsWorkQueue() actually declares today.
        await js.CreateStreamAsync(new StreamConfig(interestStream, [interestSubject])
        {
            Retention = StreamConfigRetention.Interest
        }, Ct);

        // What the comment in PartitionedMessageTopologyWithSubjects asks for.
        await js.CreateStreamAsync(new StreamConfig(workQueueStream, [workQueueSubject])
        {
            Retention = StreamConfigRetention.Workqueue
        }, Ct);

        // No consumer bound to either stream yet -- the rebalance window.
        await js.PublishAsync(interestSubject, new byte[] { 1 }, cancellationToken: Ct);
        await js.PublishAsync(workQueueSubject, new byte[] { 1 }, cancellationToken: Ct);

        (await CountStreamMessagesAsync(natsUrl, interestStream))
            .ShouldBe(0, "interest retention dropped the message outright");

        (await CountStreamMessagesAsync(natsUrl, workQueueStream))
            .ShouldBe(1, "work-queue retention held the message for the consumer that has not arrived yet");
    }

    /// <summary>
    /// GH-4845 #4. <c>NatsListener.MoveToErrorsAsync</c> returns early while
    /// <c>NumDelivered &lt; MaxDeliver</c>. A Buffered listener acks on receipt, so when Wolverine's own
    /// error policy says MoveToErrorQueue the JetStream delivery count is still 1: the guard fires, the
    /// message is neither forwarded to the dead-letter subject nor terminated, and because it was already
    /// acked the server will never redeliver it. It is simply gone.
    ///
    /// The existing <c>NatsDeadLetterSubjectTests</c> only passes because it sets maxDeliveryAttempts to 1,
    /// which makes the guard vacuous.
    ///
    /// AFTER THE FIX the dead-letter forward should happen on Wolverine's decision, not on NumDelivered.
    /// </summary>
    [Fact]
    public async Task move_to_error_queue_is_dropped_when_jetstream_has_not_hit_max_deliver()
    {
        var natsUrl = NatsTestHelpers.ResolveUrl();
        if (!await NatsTestHelpers.IsNatsAvailable(natsUrl)) return;

        var id = Guid.NewGuid().ToString("N");
        var stream = $"DLQSKIP_{id}";
        var subject = $"dlqskip.{id}.incoming";
        var deadLetterSubject = $"dlqskip-errors.{id}";
        var consumerName = $"dlqskip-consumer-{id}";

        Bug4845PoisonHandler.Reset();

        await using var dlqSubscription = await NatsTestHelpers.SubscribeRawAsync(natsUrl, deadLetterSubject);

        using var host = await Host.CreateDefaultBuilder()
            .ConfigureLogging(l => l.AddXunitLogging(_output))
            .UseWolverine(opts =>
            {
                opts.UseNats(natsUrl)
                    .AutoProvision()
                    .DefineStream(stream, s => s.WithSubjects($"dlqskip.{id}.>"));

                opts.Policies.DisableConventionalLocalRouting();

                // The default MaxDeliver (5), i.e. what a user who never calls ConfigureDeadLetterQueue
                // with an explicit 1 gets. Buffered is the default listener mode.
                opts.ListenToNatsSubject(subject)
                    .UseJetStream(stream, consumerName)
                    .DeadLetterTo(deadLetterSubject);

                opts.PublishMessage<Bug4845Poison>().ToNatsSubject(subject).UseJetStream(stream);

                // Wolverine decides this message is poison on the very first failure.
                opts.Policies.OnException<Bug4845PoisonException>().MoveToErrorQueue();
            })
            .StartAsync(cancellationToken: Ct);

        await host.MessageBus().SendAsync(new Bug4845Poison(id));

        await Bug4845PoisonHandler.WaitForAttemptAsync();

        // Wolverine asked for the error queue. Nothing arrives there.
        var deadLettered = await dlqSubscription.ReadAsync(10.Seconds());
        deadLettered.ShouldBeNull("the message WAS forwarded to the dead-letter subject -- GH-4845 #4 is fixed or does not reproduce");

        // ...and the server considers the delivery finished, so it will never come back either.
        var info = await ConsumerInfoAsync(natsUrl, stream, consumerName);
        _output.WriteLine($"consumer: NumPending={info.NumPending}, NumAckPending={info.NumAckPending}, NumRedelivered={info.NumRedelivered}, Delivered={info.Delivered.StreamSeq}/{info.AckFloor.StreamSeq}");

        info.NumAckPending.ShouldBe(0, "delivery is still outstanding, so the message is not lost yet");
        info.NumPending.ShouldBe(0ul, "the message is still waiting to be delivered, so it is not lost yet");
        Bug4845PoisonHandler.Attempts.ShouldBe(1);
    }

    private static async Task<ConsumerInfo> ConsumerInfoAsync(string natsUrl, string streamName, string consumerName)
    {
        await using var connection = new NatsConnection(new NatsOpts { Url = natsUrl });
        await connection.ConnectAsync();

        var js = connection.CreateJetStreamContext();
        var consumer = await js.GetConsumerAsync(streamName, consumerName, cancellationToken: Ct);
        return consumer.Info;
    }

    private static async Task<long> CountStreamMessagesAsync(string natsUrl, string streamName)
    {
        await using var connection = new NatsConnection(new NatsOpts { Url = natsUrl });
        await connection.ConnectAsync();

        var js = connection.CreateJetStreamContext();
        var stream = await js.GetStreamAsync(streamName, cancellationToken: Ct);
        return stream.Info.State.Messages;
    }
}

public record Bug4845Poison(string Id);

public class Bug4845PoisonException(string message) : Exception(message);

[WolverineHandler]
public static class Bug4845PoisonHandler
{
    private static TaskCompletionSource _attempted = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static int _attempts;

    public static int Attempts => _attempts;

    public static void Reset()
    {
        _attempted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Interlocked.Exchange(ref _attempts, 0);
    }

    public static void Handle(Bug4845Poison message)
    {
        Interlocked.Increment(ref _attempts);
        _attempted.TrySetResult();
        throw new Bug4845PoisonException($"This message is poison: {message.Id}");
    }

    public static Task WaitForAttemptAsync()
    {
        return _attempted.Task.WaitAsync(30.Seconds());
    }
}
