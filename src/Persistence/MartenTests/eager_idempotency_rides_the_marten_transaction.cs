using IntegrationTests;
using JasperFx.Resources;
using Marten;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine;
using Wolverine.ErrorHandling;
using Wolverine.Marten;
using Wolverine.Persistence;
using Wolverine.Runtime;
using Wolverine.Tracking;
using Wolverine.Transports;

namespace MartenTests;

/// <summary>
/// R2 (durable compliance rig). Eager idempotency on a buffered or inline endpoint used to commit a Handled inbox row
/// with its own <c>SaveChangesAsync</c> BEFORE the handler ran. When the handler then failed and the broker gave the
/// message back, the redelivery hit that row's primary key and was thrown away as a duplicate of itself -- a message
/// that was never handled. The handled row now goes into the handler's own session, so it commits or rolls back with
/// the work it records.
/// </summary>
public class eager_idempotency_rides_the_marten_transaction : IAsyncLifetime
{
    private IHost _host = null!;

    public async ValueTask InitializeAsync()
    {
        FailsTheFirstTimeHandler.Reset();

        _host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Discovery.DisableConventionalDiscovery().IncludeType(typeof(FailsTheFirstTimeHandler));
                opts.Durability.Mode = DurabilityMode.Solo;
                opts.Policies.AutoApplyTransactions(IdempotencyStyle.Eager);

                // What a broker's requeue looks like from here: this attempt is over, and a fresh delivery of the
                // same message comes later
                opts.OnException<FirstAttemptFailure>().Discard();

                opts.Services.AddMarten(m =>
                {
                    m.DisableNpgsqlLogging = true;
                    m.Connection(Servers.PostgresConnectionString);
                    m.DatabaseSchemaName = "idempotent_rides_tx";
                }).IntegrateWithWolverine();
            }).StartAsync(TestContext.Current.CancellationToken);

        await _host.ResetResourceState();
    }

    public async ValueTask DisposeAsync()
    {
        await _host.StopAsync();
        _host.Dispose();
    }

    [Fact]
    public async Task a_redelivery_after_a_failed_attempt_is_handled_and_then_deduplicated()
    {
        var id = Guid.NewGuid();
        var tracked = await _host.TrackActivity()
            .DoNotAssertOnExceptionsDetected()
            .SendMessageAndWaitAsync(new FailsTheFirstTime(id));

        var envelope = tracked.Discarded.SingleEnvelope<FailsTheFirstTime>();
        var circuit = _host.GetRuntime().Endpoints.FindListenerCircuit(envelope.Destination!)!;

        // The broker delivers it again: a new envelope, as far as the inbox knows
        var redelivered = await redeliverAsync(circuit, envelope);
        redelivered.Discarded.Envelopes().ShouldBeEmpty();

        await using (var session = _host.DocumentStore().QuerySession())
        {
            (await session.LoadAsync<HandledOnce>(id, TestContext.Current.CancellationToken)).ShouldNotBeNull();
        }

        // ...and once it HAS been handled, a further delivery really is a duplicate
        var duplicate = await redeliverAsync(circuit, envelope);
        duplicate.Discarded.SingleEnvelope<FailsTheFirstTime>().ShouldNotBeNull();
        FailsTheFirstTimeHandler.Handled(id).ShouldBe(1);
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

public record FailsTheFirstTime(Guid Id);

public class FirstAttemptFailure : Exception;

public class HandledOnce
{
    public Guid Id { get; set; }
}

public static class FailsTheFirstTimeHandler
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

    public static void Handle(FailsTheFirstTime message, IDocumentSession session)
    {
        lock (_attempts)
        {
            var attempt = _attempts[message.Id] = _attempts.GetValueOrDefault(message.Id) + 1;
            if (attempt == 1) throw new FirstAttemptFailure();

            _handled[message.Id] = _handled.GetValueOrDefault(message.Id) + 1;
        }

        session.Store(new HandledOnce { Id = message.Id });
    }
}
