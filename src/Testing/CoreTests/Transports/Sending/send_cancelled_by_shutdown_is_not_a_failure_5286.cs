using CoreTests.Runtime.WorkerQueues;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Shouldly;
using Wolverine;
using Wolverine.ComplianceTests;
using Wolverine.Logging;
using Wolverine.Persistence.Durability;
using Wolverine.Transports;
using Wolverine.Transports.Local;
using Wolverine.Transports.Sending;
using Xunit;

namespace CoreTests.Transports.Sending;

/// <summary>
/// GH-5286. Azure Service Bus hands the runtime's own cancellation token to the SDK's send, so an orderly shutdown
/// cancels a batch that is still in flight and the protocol reports that through MarkProcessingFailureAsync. That
/// is the application stopping, not a transport failure, and must not be logged at Error. A cancellation while the
/// runtime is still running is a real failure, and keeps its diagnostics
/// </summary>
public class send_cancelled_by_shutdown_is_not_a_failure_5286 : IAsyncDisposable
{
    private readonly RecordingLoggerProvider theLogs = new();
    private readonly DurabilitySettings theSettings = new();
    private readonly LocalQueue theEndpoint = new("send-cancelled-by-shutdown") { FailuresBeforeCircuitBreaks = 1 };
    private readonly DurableSendingAgent theAgent;

    public send_cancelled_by_shutdown_is_not_a_failure_5286()
    {
        var sender = Substitute.For<ISender>();
        sender.Destination.Returns(theEndpoint.Uri);

        theAgent = new DurableSendingAgent(sender, theSettings, theLogs.CreateLogger("DurableSendingAgent"),
            Substitute.For<IMessageTracker>(), Substitute.For<IMessageOutbox>(), theEndpoint);
    }

    public async ValueTask DisposeAsync()
    {
        await theAgent.DisposeAsync();
    }

    private OutgoingMessageBatch theBatch()
    {
        var envelope = ObjectMother.Envelope();
        envelope.Destination = theEndpoint.Uri;
        return new OutgoingMessageBatch(theEndpoint.Uri, [envelope]);
    }

    private IEnumerable<RecordingLoggerProvider.LogRecord> errors =>
        theLogs.Records.Where(x => x.Level >= LogLevel.Error);

    private void theSendFailureShouldBeLoggedAtError()
    {
        errors.ShouldContain(x => x.Message.StartsWith("Failure trying to send a message batch"));
        errors.ShouldContain(x => x.Message.StartsWith("Failed to send outgoing envelopes batch"));
    }

    [Fact]
    public async Task a_batch_cancelled_by_the_runtime_shutting_down_is_not_logged_as_an_error()
    {
        theSettings.Cancel();

        await theAgent.MarkProcessingFailureAsync(theBatch(), new TaskCanceledException());

        errors.ShouldBeEmpty();
        theLogs.Records.ShouldContain(x =>
            x.Level == LogLevel.Information && x.Message.Contains("interrupted by the application shutting down"));

        // Not counted toward the circuit breaker either
        theAgent.Latched.ShouldBeFalse();
    }

    [Fact]
    public async Task an_envelope_cancelled_by_the_runtime_shutting_down_is_not_logged_as_an_error()
    {
        theSettings.Cancel();

        var envelope = theBatch().Messages.Single();
        await theAgent.MarkProcessingFailureAsync(envelope, new OperationCanceledException());

        errors.ShouldBeEmpty();
        theAgent.Latched.ShouldBeFalse();
    }

    [Fact]
    public async Task a_cancellation_while_the_runtime_is_still_running_is_still_a_failure()
    {
        await theAgent.MarkProcessingFailureAsync(theBatch(),
            new OperationCanceledException("Unrelated operation cancellation"));

        theSendFailureShouldBeLoggedAtError();
        theAgent.Latched.ShouldBeTrue();
    }

    [Fact]
    public async Task a_transport_failure_during_shutdown_is_still_a_failure()
    {
        theSettings.Cancel();

        await theAgent.MarkProcessingFailureAsync(theBatch(), new InvalidOperationException("Broker unreachable"));

        theSendFailureShouldBeLoggedAtError();
    }
}
