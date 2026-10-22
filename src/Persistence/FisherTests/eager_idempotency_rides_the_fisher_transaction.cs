using JasperFx;
using Fisher;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine;
using Wolverine.ErrorHandling;
using Wolverine.Fisher;
using Wolverine.Persistence;
using Wolverine.Runtime;
using Wolverine.Tracking;
using Wolverine.Transports;

namespace FisherTests;

/// <summary>
/// R2 (durable compliance rig). Eager idempotency on a buffered or inline endpoint used to commit a Handled inbox row
/// with its own <c>SaveChangesAsync</c> BEFORE the handler ran (the Fisher twin of the Marten finding). When the handler then failed and the broker gave the
/// message back, the redelivery hit that row's primary key and was thrown away as a duplicate of itself -- a message
/// that was never handled. The handled row now goes into the handler's own session, so it commits or rolls back with
/// the work it records.
/// </summary>
public class eager_idempotency_rides_the_fisher_transaction : IAsyncLifetime
{
    private IHost _host = null!;
    private FisherTestDatabase theDatabase = null!;

    public async ValueTask InitializeAsync()
    {
        FisherFailsTheFirstTimeHandler.Reset();
        theDatabase = Servers.CreateDatabase("idempotent_rides_tx");

        _host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Discovery.DisableConventionalDiscovery().IncludeType(typeof(FisherFailsTheFirstTimeHandler));
                opts.Durability.Mode = DurabilityMode.Solo;
                opts.Policies.AutoApplyTransactions(IdempotencyStyle.Eager);

                // What a broker's requeue looks like from here: this attempt is over, and a fresh delivery of the
                // same message comes later
                opts.OnException<FisherFirstAttemptFailure>().Discard();

                opts.Services.AddFisher(m =>
                {
                    m.Connection(theDatabase.ConnectionString);
                    m.AutoCreateSchemaObjects = AutoCreate.All;
                }).ApplyAllDatabaseChangesOnStartup().IntegrateWithWolverine();
            }).StartAsync(TestContext.Current.CancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        await _host.StopAsync();
        _host.Dispose();
        theDatabase.Dispose();
    }

    [Fact]
    public async Task a_redelivery_after_a_failed_attempt_is_handled_and_then_deduplicated()
    {
        var id = Guid.NewGuid();
        var tracked = await _host.TrackActivity()
            .DoNotAssertOnExceptionsDetected()
            .SendMessageAndWaitAsync(new FisherFailsTheFirstTime(id));

        var envelope = tracked.Discarded.SingleEnvelope<FisherFailsTheFirstTime>();
        var circuit = _host.GetRuntime().Endpoints.FindListenerCircuit(envelope.Destination!)!;

        // The broker delivers it again: a new envelope, as far as the inbox knows
        var redelivered = await redeliverAsync(circuit, envelope);
        redelivered.Discarded.Envelopes().ShouldBeEmpty();

        await using (var session = _host.Services.GetRequiredService<IDocumentStore>().QuerySession())
        {
            (await session.LoadAsync<FisherHandledOnce>(id, TestContext.Current.CancellationToken)).ShouldNotBeNull();
        }

        // ...and once it HAS been handled, a further delivery really is a duplicate
        var duplicate = await redeliverAsync(circuit, envelope);
        duplicate.Discarded.SingleEnvelope<FisherFailsTheFirstTime>().ShouldNotBeNull();
        FisherFailsTheFirstTimeHandler.Handled(id).ShouldBe(1);
    }

    private Task<ITrackedSession> redeliverAsync(IListenerCircuit circuit, Envelope envelope)
    {
        return _host.TrackActivity()
            .DoNotAssertOnExceptionsDetected()
            .ExecuteAndWaitAsync(_ =>
            {
                envelope.WasPersistedInInbox = false;
                envelope.Status = EnvelopeStatus.Incoming;
                envelope.Attempts = 0;
                return circuit.EnqueueDirectlyAsync([envelope]);
            });
    }
}

public record FisherFailsTheFirstTime(Guid Id);

public class FisherFirstAttemptFailure : Exception;

public class FisherHandledOnce
{
    public Guid Id { get; set; }
}

public static class FisherFailsTheFirstTimeHandler
{
    private static readonly Dictionary<Guid, int> _attempts = new();
    private static readonly Dictionary<Guid, int> _handled = new();

    public static void Reset()
    {
        lock (_attempts)
        {
            _attempts.Clear();
            _handled.Clear();
        }
    }

    public static int Handled(Guid id)
    {
        lock (_attempts) return _handled.GetValueOrDefault(id);
    }

    public static void Handle(FisherFailsTheFirstTime message, IDocumentSession session)
    {
        lock (_attempts)
        {
            var attempt = _attempts[message.Id] = _attempts.GetValueOrDefault(message.Id) + 1;
            if (attempt == 1) throw new FisherFirstAttemptFailure();

            _handled[message.Id] = _handled.GetValueOrDefault(message.Id) + 1;
        }

        session.Store(new FisherHandledOnce { Id = message.Id });
    }
}
