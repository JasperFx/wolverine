using System.Text;
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
/// An error policy can send a message to the dead letter queue on its first failure, long before the consumer's
/// <c>MaxDeliver</c> is used up. A Buffered JetStream listener acknowledges each delivery as it receives it, and an
/// Inline one acknowledges it once the dead letter move completes, so JetStream will not deliver it again: the
/// dead letter subject is the only place the message can still go.
/// </summary>
[Collection("NATS Integration")]
[Trait("Category", "Integration")]
public class NatsEarlyDeadLetterTests
{
    private readonly NatsContainerFixture _fixture;
    private readonly ITestOutputHelper _output;

    public NatsEarlyDeadLetterTests(NatsContainerFixture fixture, ITestOutputHelper output)
    {
        _fixture = fixture;
        _output = output;
    }

    [Fact]
    public Task buffered_listener_dead_letters_on_the_first_failure()
    {
        return assertDeadLetteredOnFirstFailureAsync(inline: false);
    }

    [Fact]
    public Task inline_listener_dead_letters_on_the_first_failure()
    {
        return assertDeadLetteredOnFirstFailureAsync(inline: true);
    }

    [Fact]
    public async Task dead_letter_copy_in_the_same_stream_is_not_dropped_as_a_duplicate()
    {
        var id = Guid.NewGuid().ToString("N");
        var stream = $"SAMESTREAMDLQ_{id}";
        var subject = $"samestreamdlq.{id}.incoming";

        // Covered by the same stream as the original, which already holds a message with the original's
        // Nats-Msg-Id, well inside the stream's duplicate window
        var deadLetterSubject = $"samestreamdlq.{id}.dead-letters";

        EarlyPoisonHandler.Reset();

        using var host = await Host.CreateDefaultBuilder()
            .ConfigureLogging(l => l.AddXunitLogging(_output))
            .UseWolverine(opts =>
            {
                opts.ServiceName = "SameStreamDeadLetter";
                opts.UseNats(_fixture.ConnectionString)
                    .AutoProvision()
                    .DefineStream(stream, s => s.WithSubjects($"samestreamdlq.{id}.>"));

                opts.Policies.DisableConventionalLocalRouting();
                opts.ListenToNatsSubject(subject)
                    .UseJetStream(stream, $"samestreamdlq-{id}")
                    .DeadLetterTo(deadLetterSubject);

                opts.Policies.OnException<EarlyPoisonException>().MoveToErrorQueue();
            })
            .StartAsync(TestContext.Current.CancellationToken);

        using var sender = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.ServiceName = "SameStreamDeadLetterSender";
                opts.UseNats(_fixture.ConnectionString);
                opts.Policies.DisableConventionalLocalRouting();
                opts.PublishMessage<EarlyPoisonMessage>().ToNatsSubject(subject).UseJetStream(stream);
            })
            .StartAsync(TestContext.Current.CancellationToken);

        await sender.MessageBus().PublishAsync(new EarlyPoisonMessage(id));

        await using var connection = new NatsConnection(new NatsOpts { Url = _fixture.ConnectionString });
        await connection.ConnectAsync();
        var js = connection.CreateJetStreamContext();

        long deadLettered = 0;
        var deadline = DateTimeOffset.UtcNow.Add(10.Seconds());
        while (deadLettered == 0 && DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(250, TestContext.Current.CancellationToken);
            var info = await js.GetStreamAsync(stream, new StreamInfoRequest { SubjectsFilter = deadLetterSubject },
                TestContext.Current.CancellationToken);
            deadLettered = info.Info.State.Subjects?.TryGetValue(deadLetterSubject, out var count) == true ? count : 0;
        }

        _output.WriteLine($"handler attempts: {EarlyPoisonHandler.Attempts}, dead-lettered copies: {deadLettered}");
        EarlyPoisonHandler.Attempts.ShouldBe(1);
        deadLettered.ShouldBe(1);
    }

    private async Task assertDeadLetteredOnFirstFailureAsync(bool inline)
    {
        var id = Guid.NewGuid().ToString("N");
        var stream = $"EARLYDLQ_{id}";
        var subject = $"earlydlq.{id}.incoming";
        var deadLetterSubject = $"earlydlq-errors.{id}";
        var consumer = $"earlydlq-{id}";

        EarlyPoisonHandler.Reset();

        await using var deadLetters = await NatsTestHelpers.SubscribeRawAsync(_fixture.ConnectionString, deadLetterSubject);

        using var host = await Host.CreateDefaultBuilder()
            .ConfigureLogging(l => l.AddXunitLogging(_output))
            .UseWolverine(opts =>
            {
                opts.ServiceName = "EarlyDeadLetter";
                opts.UseNats(_fixture.ConnectionString)
                    .AutoProvision()
                    .DefineStream(stream, s => s.WithSubjects($"earlydlq.{id}.>"));

                opts.Policies.DisableConventionalLocalRouting();

                // MaxDeliver stays at the default of 5, and a short AckWait would bring an unacknowledged
                // delivery back quickly
                var listener = opts.ListenToNatsSubject(subject)
                    .UseJetStream(stream, consumer)
                    .AckWait(2.Seconds())
                    .DeadLetterTo(deadLetterSubject);

                if (inline)
                {
                    listener.ProcessInline();
                }
                else
                {
                    listener.BufferedInMemory();
                }

                // Straight to the dead letter queue, no retries
                opts.Policies.OnException<EarlyPoisonException>().MoveToErrorQueue();
            })
            .StartAsync(TestContext.Current.CancellationToken);

        // A separate sender, so the publishing rule cannot share -- and change -- the listener's endpoint
        using var sender = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.ServiceName = "EarlyDeadLetterSender";
                opts.UseNats(_fixture.ConnectionString);
                opts.Policies.DisableConventionalLocalRouting();
                opts.PublishMessage<EarlyPoisonMessage>().ToNatsSubject(subject).UseJetStream(stream);
            })
            .StartAsync(TestContext.Current.CancellationToken);

        await sender.MessageBus().PublishAsync(new EarlyPoisonMessage(id));

        var deadLettered = await deadLetters.ReadAsync(10.Seconds());

        _output.WriteLine($"handler attempts: {EarlyPoisonHandler.Attempts}");
        await using (var connection = new NatsConnection(new NatsOpts { Url = _fixture.ConnectionString }))
        {
            await connection.ConnectAsync();
            var info = (await connection.CreateJetStreamContext()
                .GetConsumerAsync(stream, consumer, TestContext.Current.CancellationToken)).Info;
            _output.WriteLine(
                $"consumer: delivered={info.Delivered.ConsumerSeq} ack floor={info.AckFloor.ConsumerSeq} pending={info.NumPending} ack pending={info.NumAckPending} redelivered={info.NumRedelivered}");
        }

        deadLettered.ShouldNotBeNull();
        Encoding.UTF8.GetString(deadLettered.Value.Data!).ShouldContain(id);
    }
}

public record EarlyPoisonMessage(string Id);

/// <summary>
/// Keeps the stack trace on one line. On Windows a real stack trace contains CRLF, which NATS refuses in the
/// exception-stack header of the dead letter copy -- a separate problem this test must not trip over.
/// </summary>
public class EarlyPoisonException : Exception
{
    public EarlyPoisonException(string message) : base(message)
    {
    }

    public override string StackTrace => "at EarlyPoisonHandler.Handle";
}

[WolverineHandler]
public static class EarlyPoisonHandler
{
    private static int _attempts;

    public static int Attempts => Volatile.Read(ref _attempts);

    public static void Reset()
    {
        Interlocked.Exchange(ref _attempts, 0);
    }

    public static void Handle(EarlyPoisonMessage message)
    {
        Interlocked.Increment(ref _attempts);
        throw new EarlyPoisonException($"This message is poison: {message.Id}");
    }
}
