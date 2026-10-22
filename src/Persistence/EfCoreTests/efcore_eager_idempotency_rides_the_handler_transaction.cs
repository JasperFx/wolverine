using IntegrationTests;
using JasperFx;
using JasperFx.Resources;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using SharedPersistenceModels.Items;
using Shouldly;
using Wolverine;
using Wolverine.EntityFrameworkCore;
using Wolverine.ErrorHandling;
using Wolverine.Persistence;
using Wolverine.Runtime;
using Wolverine.SqlServer;
using Wolverine.Tracking;
using Wolverine.Transports;

namespace EfCoreTests;

/// <summary>
/// E1 and R2 (durable compliance rig). Eager idempotency on a buffered or inline endpoint puts a Handled inbox row
/// into the handler's transaction before the handler runs, and stamps the envelope as persisted.
/// <list type="bullet">
/// <item>E1: when the attempt failed, the rollback took the row with it but the envelope stayed stamped, so the
/// retry skipped the check, its commit marked a row that no longer existed, and no record of completion survived
/// for a later redelivery to find.</item>
/// <item>R2: in Lightweight mode the check committed the row on its own <c>SaveChangesAsync</c>, before the
/// handler ran, so a failed attempt left a record of completion behind and the redelivery of a message that was
/// never handled was thrown away as a duplicate of itself.</item>
/// </list>
/// </summary>
public class efcore_eager_idempotency_rides_the_handler_transaction : IAsyncLifetime
{
    public ValueTask InitializeAsync()
    {
        EfFailsOnceHandler.Reset();
        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        SqlConnection.ClearAllPools();
        return ValueTask.CompletedTask;
    }

    private static Task<IHost> startHostAsync(TransactionMiddlewareMode mode, bool wolverineMapped)
    {
        return Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Durability.Mode = DurabilityMode.Solo;

                if (wolverineMapped)
                {
                    opts.Services.AddDbContextWithWolverineIntegration<CleanDbContext>(x =>
                        x.UseSqlServer(Servers.SqlServerConnectionString));
                }
                else
                {
                    opts.Services.AddDbContext<CleanDbContext>(x => x.UseSqlServer(Servers.SqlServerConnectionString));
                }

                opts.PersistMessagesWithSqlServer(Servers.SqlServerConnectionString, "idempotent_rides_tx");
                opts.UseEntityFrameworkCoreTransactions(mode);
                opts.Policies.AutoApplyTransactions(IdempotencyStyle.Eager);

                opts.OnException<RetriedFailure>().RetryOnce();

                // What a broker's requeue looks like from here: this attempt is over, and a fresh delivery of the
                // same message comes later
                opts.OnException<DiscardedFailure>().Discard();

                opts.Discovery.DisableConventionalDiscovery().IncludeType(typeof(EfFailsOnceHandler));

                opts.UseEntityFrameworkCoreWolverineManagedMigrations();
                opts.Services.AddResourceSetupOnStartup(StartupAction.ResetState);
            }).StartAsync();
    }

    [Theory]
    [InlineData(TransactionMiddlewareMode.Eager, true)]
    [InlineData(TransactionMiddlewareMode.Eager, false)]
    [InlineData(TransactionMiddlewareMode.Lightweight, true)]
    [InlineData(TransactionMiddlewareMode.Lightweight, false)]
    public async Task a_retried_attempt_still_leaves_a_record_of_completion(TransactionMiddlewareMode mode,
        bool wolverineMapped)
    {
        using var host = await startHostAsync(mode, wolverineMapped);

        var id = Guid.NewGuid();
        var tracked = await host.TrackActivity()
            .DoNotAssertOnExceptionsDetected()
            .SendMessageAndWaitAsync(new EfFailsOnce(id, Retry: true));

        EfFailsOnceHandler.Handled(id).ShouldBe(1);
        var envelope = tracked.Executed.Envelopes().First(x => x.Message is EfFailsOnce);

        // A later delivery of the same message has to find the retry's record of completion
        var duplicate = await redeliverAsync(host, envelope);
        duplicate.Discarded.SingleEnvelope<EfFailsOnce>().ShouldNotBeNull();
        EfFailsOnceHandler.Handled(id).ShouldBe(1);
    }

    [Theory]
    [InlineData(TransactionMiddlewareMode.Eager, true)]
    [InlineData(TransactionMiddlewareMode.Eager, false)]
    [InlineData(TransactionMiddlewareMode.Lightweight, true)]
    [InlineData(TransactionMiddlewareMode.Lightweight, false)]
    public async Task a_redelivery_after_a_failed_attempt_is_handled(TransactionMiddlewareMode mode, bool wolverineMapped)
    {
        using var host = await startHostAsync(mode, wolverineMapped);

        var id = Guid.NewGuid();
        var tracked = await host.TrackActivity()
            .DoNotAssertOnExceptionsDetected()
            .SendMessageAndWaitAsync(new EfFailsOnce(id, Retry: false));

        EfFailsOnceHandler.Handled(id).ShouldBe(0);
        var envelope = tracked.Discarded.SingleEnvelope<EfFailsOnce>();

        // The broker delivers it again: a new envelope, as far as the inbox knows
        var redelivered = await redeliverAsync(host, envelope);
        redelivered.Discarded.Envelopes().ShouldBeEmpty();
        EfFailsOnceHandler.Handled(id).ShouldBe(1);
    }

    private static Task<ITrackedSession> redeliverAsync(IHost host, Envelope envelope)
    {
        var circuit = host.GetRuntime().Endpoints.FindListenerCircuit(envelope.Destination!)!;

        return host.TrackActivity()
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

public record EfFailsOnce(Guid Id, bool Retry);

public class RetriedFailure : Exception;

public class DiscardedFailure : Exception;

public static class EfFailsOnceHandler
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

    public static void Handle(EfFailsOnce message, CleanDbContext dbContext)
    {
        lock (_attempts)
        {
            var attempt = _attempts[message.Id] = _attempts.GetValueOrDefault(message.Id) + 1;
            if (attempt == 1)
            {
                throw message.Retry ? new RetriedFailure() : new DiscardedFailure();
            }

            _handled[message.Id] = _handled.GetValueOrDefault(message.Id) + 1;
        }

        dbContext.Items.Add(new Item { Id = message.Id, Name = "handled once" });
    }
}
