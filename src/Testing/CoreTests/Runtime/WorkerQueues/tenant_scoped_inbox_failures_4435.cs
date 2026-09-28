using Microsoft.Extensions.Logging;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Shouldly;
using Wolverine;
using Wolverine.ComplianceTests;
using Wolverine.Persistence.Durability;
using Wolverine.Runtime;
using Wolverine.Runtime.WorkerQueues;
using Wolverine.Transports;
using Wolverine.Transports.Stub;
using Xunit;

namespace CoreTests.Runtime.WorkerQueues;

/// <summary>
/// GH-4435. A durable listener fronting database-per-tenant message storage receives batches that span
/// tenants. When one tenant's database cannot take its half of the batch, two things have to hold:
/// the envelopes that were never stored must NOT be acked, and the listener must NOT pause -- it serves
/// every other tenant, and those are fine. A failure that reached the MAIN store is the exception: the
/// listener pauses for inbox recovery exactly as it always has.
/// </summary>
public class tenant_scoped_inbox_failures_4435
{
    private readonly Envelope theCommittedEnvelope = ObjectMother.Envelope();
    private readonly Envelope theUnpersistedEnvelope = ObjectMother.Envelope();
    private readonly IListener theListener = Substitute.For<IListener>();
    private readonly IHandlerPipeline thePipeline = Substitute.For<IHandlerPipeline>();
    private readonly CapturedLogs theLogs = new();
    private readonly DurableReceiver theReceiver;
    private readonly MockWolverineRuntime theRuntime;

    public tenant_scoped_inbox_failures_4435()
    {
        theRuntime = new MockWolverineRuntime();
        theRuntime.LoggerFactory.AddProvider(theLogs);

        theReceiver = new DurableReceiver(new StubEndpoint("one", new StubTransport()), theRuntime, thePipeline);

        theCommittedEnvelope.TenantId = "healthy";
        theUnpersistedEnvelope.TenantId = "downed";
    }

    private static TenantedInboxWriteException tenantFailure(Envelope unpersisted, bool includesMainStore)
    {
        return new TenantedInboxWriteException([unpersisted], includesMainStore,
            [new TimeoutException("tenant database is unreachable")]);
    }

    /// <summary>
    /// Drive the batch path with a store that fails the way MultiTenantedMessageStore now fails: the
    /// group that committed is stamped WasPersistedInInbox before the throw, so only the envelope that
    /// never landed is left for the per-envelope fallback to deal with.
    /// </summary>
    private async Task theBatchFailsWith(Exception exception)
    {
        theRuntime.Storage.Inbox
            .StoreIncomingAsync(Arg.Any<IReadOnlyList<Envelope>>())
            .Throws(_ =>
            {
                theCommittedEnvelope.WasPersistedInInbox = true;
                return exception;
            });

        // The fallback re-attempts the unpersisted envelope one at a time, where its own store fails again.
        theRuntime.Storage.Inbox
            .StoreIncomingAsync(theUnpersistedEnvelope)
            .Throws(tenantFailure(theUnpersistedEnvelope, false));

        await theReceiver.ProcessReceivedMessagesAsync(DateTimeOffset.UtcNow, theListener,
            [theCommittedEnvelope, theUnpersistedEnvelope]);

        await theReceiver.DrainAsync();
    }

    [Fact]
    public async Task one_downed_tenant_database_does_not_pause_the_listener()
    {
        await theBatchFailsWith(tenantFailure(theUnpersistedEnvelope, false));

        theReceiver.InboxUnavailableSignaled.ShouldBeFalse(
            "a single tenant database outage must not stop a listener serving every other tenant");
    }

    [Fact]
    public async Task the_envelope_that_was_never_stored_is_deferred_rather_than_acked()
    {
        await theBatchFailsWith(tenantFailure(theUnpersistedEnvelope, false));

        // This is the heart of the bug: it used to be acked and handed to the handler pipeline with no
        // inbox row anywhere, so nothing could ever recover it.
        await theListener.Received().DeferAsync(theUnpersistedEnvelope);
        await theListener.DidNotReceive().CompleteAsync(theUnpersistedEnvelope);
    }

    [Fact]
    public async Task the_envelope_whose_store_committed_is_still_acked()
    {
        await theBatchFailsWith(tenantFailure(theUnpersistedEnvelope, false));

        await theListener.Received().CompleteAsync(theCommittedEnvelope);
    }

    [Fact]
    public async Task the_envelope_whose_store_committed_is_not_stored_a_second_time()
    {
        await theBatchFailsWith(tenantFailure(theUnpersistedEnvelope, false));

        // Re-storing it would throw DuplicateIncomingEnvelopeException, and a duplicate is settled at the
        // listener WITHOUT being handled -- silent loss on the tenant that was healthy all along.
        await theRuntime.Storage.Inbox.DidNotReceive().StoreIncomingAsync(theCommittedEnvelope);
    }

    [Fact]
    public async Task a_failure_that_reached_the_main_store_still_pauses_the_listener()
    {
        await theBatchFailsWith(tenantFailure(theUnpersistedEnvelope, true));

        theReceiver.InboxUnavailableSignaled.ShouldBeTrue();
    }

    [Fact]
    public async Task an_untyped_persistence_failure_still_pauses_the_listener()
    {
        // Every non-tenanted store throws plain exceptions, and their behavior is unchanged.
        await theBatchFailsWith(new TimeoutException("the whole database is gone"));

        theReceiver.InboxUnavailableSignaled.ShouldBeTrue();
    }

    /// <summary>
    /// GH-4658. A tenant-scoped failure already gets its own Warning naming the same endpoint and count, and
    /// while that tenant's database is down its envelopes are redelivered continuously -- one stranded
    /// message produced ~1,450 of these Errors in 19 seconds in the reporter's repro. An Error per turn over
    /// a condition the listener is deliberately riding out buries everything else in the log.
    /// </summary>
    [Fact]
    public async Task a_tenant_scoped_batch_failure_is_not_logged_as_an_error()
    {
        await theBatchFailsWith(tenantFailure(theUnpersistedEnvelope, false));

        theLogs.Errors.ShouldBeEmpty();

        // ...and the tenant-scoped Warning is still there, so the outage is not silent either
        theLogs.Warnings.ShouldContain(x => x.Contains("Inbox write failed for"));
    }

    [Fact]
    public async Task a_main_store_batch_failure_is_still_logged_as_an_error()
    {
        await theBatchFailsWith(tenantFailure(theUnpersistedEnvelope, true));

        theLogs.Errors.ShouldContain(x => x.Contains("Error trying to persist incoming envelopes"));
    }
}

/// <summary>
/// Collects the messages one <see cref="ILoggerFactory" /> was asked to write, by level.
/// </summary>
internal class CapturedLogs : ILoggerProvider, ILogger
{
    private readonly List<string> _errors = new();
    private readonly List<string> _warnings = new();

    public IReadOnlyList<string> Errors
    {
        get { lock (_errors) return _errors.ToArray(); }
    }

    public IReadOnlyList<string> Warnings
    {
        get { lock (_warnings) return _warnings.ToArray(); }
    }

    public ILogger CreateLogger(string categoryName) => this;

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        var target = logLevel switch
        {
            LogLevel.Error or LogLevel.Critical => _errors,
            LogLevel.Warning => _warnings,
            _ => null
        };

        if (target == null) return;

        var message = formatter(state, exception);
        lock (target) target.Add(message);
    }

    public void Dispose()
    {
    }
}
