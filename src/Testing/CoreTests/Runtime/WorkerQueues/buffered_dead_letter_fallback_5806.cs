using System.Collections.Concurrent;
using JasperFx.Core;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Shouldly;
using Wolverine;
using Wolverine.ComplianceTests;
using Wolverine.Configuration;
using Wolverine.Persistence.Durability;
using Wolverine.Runtime;
using Wolverine.Runtime.WorkerQueues;
using Wolverine.Transports;
using Wolverine.Transports.Sending;
using Wolverine.Transports.Stub;
using Xunit;

namespace CoreTests.Runtime.WorkerQueues;

/// <summary>
/// GH-5806. A buffered endpoint settles each delivery with the broker as it arrives, so once the handler gives up the
/// envelope in memory is the only copy. Its dead-letter move went to a <c>RetryBlock</c>: one attempt inline, at most
/// three in the background, then a discard logged at Information. Nothing at all happened once the host was stopping,
/// and a retry still waiting when the receiver drained was dropped. A dead letter queue that was missing, refused the
/// send or was briefly unavailable lost the message with no trace beyond the log.
///
/// <para>
/// When every send fails, or the host stops before one succeeds, the envelope now goes to the message store's dead
/// letter storage. With no message store it is still discarded, but with an error naming it. A send that succeeds
/// within the retries goes only to the native dead letter queue, as before.
/// </para>
/// </summary>
public class buffered_dead_letter_fallback_5806 : IAsyncLifetime
{
    private readonly MockWolverineRuntime theRuntime = new();
    private readonly RecordingLoggerProvider theLogs = new();
    private readonly FlakyDeadLetterSender theDeadLetterQueue = new();
    private readonly Envelope theEnvelope = ObjectMother.Envelope();
    private readonly Exception theException = new DivideByZeroException();
    private readonly ConcurrentBag<Envelope> theDeadLetterRows = new();
    private BufferedReceiver theReceiver = null!;

    public ValueTask InitializeAsync()
    {
        theRuntime.LoggerFactory.AddProvider(theLogs);

        theRuntime.Storage.Inbox.MoveToDeadLetterStorageAsync(Arg.Any<Envelope>(), Arg.Any<Exception?>())
            .Returns(call =>
            {
                theDeadLetterRows.Add(call.Arg<Envelope>());
                return Task.CompletedTask;
            });

        var endpoint = new DeadLetteringStubEndpoint("buffered-5806", theDeadLetterQueue);
        theReceiver = new BufferedReceiver(endpoint, theRuntime, Substitute.For<IHandlerPipeline>());

        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        theReceiver.SafeDispose();
        return ValueTask.CompletedTask;
    }

    [Fact]
    public async Task a_dead_letter_queue_that_never_takes_the_send_falls_back_to_dead_letter_storage()
    {
        theDeadLetterQueue.FailuresBeforeSuccess = int.MaxValue;

        await theReceiver.MoveToErrorsAsync(theEnvelope, theException);

        await waitForDeadLetterRowAsync();
        await theRuntime.Storage.Inbox.Received(1).MoveToDeadLetterStorageAsync(theEnvelope, theException);

        // One attempt inline and three in the background, the same budget as before
        theDeadLetterQueue.Attempts.ShouldBe(4);
        theDeadLetterQueue.Sent.ShouldBeEmpty();

        theLogs.Records.ShouldContain(x =>
            x.Level == LogLevel.Error && x.Message.Contains(theEnvelope.Id.ToString()) &&
            x.Message.Contains("dead letter storage"));
    }

    [Fact]
    public async Task without_a_message_store_the_discard_is_logged_as_an_error_naming_the_envelope()
    {
        theRuntime.Storage = new NullMessageStore();
        theDeadLetterQueue.FailuresBeforeSuccess = int.MaxValue;

        await theReceiver.MoveToErrorsAsync(theEnvelope, theException);

        await Poll.UntilAsync(() => theLogs.Records.Any(isTheDiscardError));
        theLogs.Records.Count(isTheDiscardError).ShouldBe(1);
    }

    [Fact]
    public async Task a_send_that_recovers_within_the_retries_goes_only_to_the_native_dead_letter_queue()
    {
        theDeadLetterQueue.FailuresBeforeSuccess = 2;

        await theReceiver.MoveToErrorsAsync(theEnvelope, theException);

        await Poll.UntilAsync(() => theDeadLetterQueue.Sent.Count == 1);
        await theReceiver.DrainAsync();

        theDeadLetterQueue.Sent.Single().ShouldBeSameAs(theEnvelope);
        theDeadLetterQueue.Attempts.ShouldBe(3);
        theDeadLetterRows.ShouldBeEmpty();
    }

    [Fact]
    public async Task a_send_that_succeeds_first_time_goes_only_to_the_native_dead_letter_queue()
    {
        await theReceiver.MoveToErrorsAsync(theEnvelope, theException);

        theDeadLetterQueue.Sent.Single().ShouldBeSameAs(theEnvelope);
        await theReceiver.DrainAsync();
        theDeadLetterRows.ShouldBeEmpty();
    }

    [Fact]
    public async Task a_retry_still_failing_when_the_receiver_drains_falls_back_before_the_drain_returns()
    {
        theDeadLetterQueue.FailuresBeforeSuccess = int.MaxValue;

        await theReceiver.MoveToErrorsAsync(theEnvelope, theException);

        // The endpoints drain before the host cancels anything and before the message store shuts down
        await theReceiver.DrainAsync();

        theDeadLetterRows.ShouldHaveSingleItem().ShouldBeSameAs(theEnvelope);
    }

    [Fact]
    public async Task a_retry_cut_short_by_the_host_stopping_falls_back_to_dead_letter_storage()
    {
        theDeadLetterQueue.FailuresBeforeSuccess = int.MaxValue;

        await theReceiver.MoveToErrorsAsync(theEnvelope, theException);
        theRuntime.DurabilitySettings.Cancel();

        await theReceiver.DrainAsync();

        theDeadLetterRows.ShouldHaveSingleItem().ShouldBeSameAs(theEnvelope);
        theDeadLetterQueue.Attempts.ShouldBeLessThan(4);
    }

    [Fact]
    public async Task a_failed_send_after_the_host_stopped_falls_back_to_dead_letter_storage()
    {
        theDeadLetterQueue.FailuresBeforeSuccess = int.MaxValue;
        theRuntime.DurabilitySettings.Cancel();

        await theReceiver.MoveToErrorsAsync(theEnvelope, theException);
        await theReceiver.DrainAsync();

        theDeadLetterRows.ShouldHaveSingleItem().ShouldBeSameAs(theEnvelope);
    }

    private bool isTheDiscardError(RecordingLoggerProvider.LogRecord record)
    {
        return record.Level == LogLevel.Error && record.Message.Contains(theEnvelope.Id.ToString()) &&
               record.Message.Contains("discarded");
    }

    private Task waitForDeadLetterRowAsync() => Poll.UntilAsync(() => !theDeadLetterRows.IsEmpty);

    private static class Poll
    {
        public static async Task UntilAsync(Func<bool> condition)
        {
            var timeout = DateTimeOffset.UtcNow.Add(10.Seconds());
            while (!condition())
            {
                if (DateTimeOffset.UtcNow > timeout) throw new TimeoutException("The condition was never met");
                await Task.Delay(25.Milliseconds(), TestContext.Current.CancellationToken);
            }
        }
    }

    private class DeadLetteringStubEndpoint : StubEndpoint
    {
        private readonly ISender _deadLetterSender;

        public DeadLetteringStubEndpoint(string name, ISender deadLetterSender) : base(name, new StubTransport())
        {
            _deadLetterSender = deadLetterSender;
            Mode = EndpointMode.BufferedInMemory;
        }

        public override bool TryBuildDeadLetterSender(IWolverineRuntime runtime, out ISender? deadLetterSender)
        {
            deadLetterSender = _deadLetterSender;
            return true;
        }
    }

    /// <summary>Refuses the first <see cref="FailuresBeforeSuccess" /> sends, then takes them</summary>
    private class FlakyDeadLetterSender : ISender
    {
        private int _attempts;

        public int FailuresBeforeSuccess { get; set; }
        public int Attempts => _attempts;
        public ConcurrentBag<Envelope> Sent { get; } = new();

        public bool SupportsNativeScheduledSend => false;
        public Uri Destination { get; } = new("stub://buffered-5806-dlq");
        public Task<bool> PingAsync() => Task.FromResult(true);

        public ValueTask SendAsync(Envelope envelope)
        {
            var attempt = Interlocked.Increment(ref _attempts);
            if (attempt <= FailuresBeforeSuccess)
            {
                throw new InvalidOperationException("The dead letter queue does not exist");
            }

            Sent.Add(envelope);
            return ValueTask.CompletedTask;
        }
    }
}
