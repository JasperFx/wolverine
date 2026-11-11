using JasperFx.Core;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine;
using Wolverine.Sqlite;
using Wolverine.Sqlite.Transport;
using Wolverine.Tracking;

namespace SqliteTests.Transport;

/// <summary>
/// GH-5511. RestoreV6Defaults() gives a new SQLite queue table the 6.x layout (no ordering indexes), an explicit
/// EnableDeterministicQueueOrdering() still wins, and an existing table keeps the layout it has.
/// </summary>
public class restore_v6_queue_defaults : SqliteContext
{
    private const string QueueName = "restored";

    private static CancellationToken ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task restore_alone_gives_a_new_queue_table_the_6x_layout(bool restoreFirst)
    {
        using var database = Servers.CreateDatabase(nameof(restore_v6_queue_defaults));
        using var host = await startAsync(database, restoreFirst, explicitOrdering: null);
        var queue = queueOf(host);

        queue.Parent.EnableDeterministicQueueOrdering.ShouldBeFalse();
        (await queue_ordering_is_opt_in.indexesOnAsync(database, queue.QueueTable)).ShouldBeEmpty();
        (await queue_ordering_is_opt_in.indexesOnAsync(database, queue.ScheduledTable)).ShouldBeEmpty();
        (await queue.CheckAsync()).ShouldBeTrue();

        await host.StopAsync(ct);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task an_explicit_opt_in_wins_over_the_restored_default(bool restoreFirst)
    {
        using var database = Servers.CreateDatabase(nameof(restore_v6_queue_defaults));
        using var host = await startAsync(database, restoreFirst, explicitOrdering: true);
        var queue = queueOf(host);

        queue.Parent.EnableDeterministicQueueOrdering.ShouldBeTrue();
        (await queue_ordering_is_opt_in.indexesOnAsync(database, queue.QueueTable)).ShouldNotBeEmpty();
        (await queue_ordering_is_opt_in.indexesOnAsync(database, queue.ScheduledTable)).ShouldNotBeEmpty();
        (await queue.CheckAsync()).ShouldBeTrue();

        await host.StopAsync(ct);
    }

    [Fact]
    public async Task an_existing_ordered_table_keeps_its_layout_after_restore()
    {
        using var database = Servers.CreateDatabase(nameof(restore_v6_queue_defaults));

        using (var current = await startAsync(database, restore: false, restoreFirst: false, explicitOrdering: null))
        {
            (await queue_ordering_is_opt_in.indexesOnAsync(database, queueOf(current).QueueTable)).ShouldNotBeEmpty();
            await current.StopAsync(ct);
        }

        using var restored = await startAsync(database, restoreFirst: false, explicitOrdering: null,
            keepEnvelopeStorageLayout: true);
        var queue = queueOf(restored);

        queue.Parent.EnableDeterministicQueueOrdering.ShouldBeFalse();
        (await queue_ordering_is_opt_in.indexesOnAsync(database, queue.QueueTable)).ShouldNotBeEmpty();
        (await queue_ordering_is_opt_in.indexesOnAsync(database, queue.ScheduledTable)).ShouldNotBeEmpty();
        (await queue.CheckAsync()).ShouldBeTrue();

        await restored.StopAsync(ct);
    }

    private static async Task<IHost> startAsync(SqliteTestDatabase database, bool restoreFirst, bool? explicitOrdering,
        bool restore = true, bool keepEnvelopeStorageLayout = false)
    {
        return await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                if (restore && restoreFirst) opts.RestoreV6Defaults();

                opts.Durability.Mode = DurabilityMode.Solo;
                opts.Discovery.DisableConventionalDiscovery();
                opts.Durability.ScheduledJobFirstExecution = 1.Hours();
                opts.Durability.ScheduledJobPollingTime = 1.Hours();
                var expression = opts.UseSqlitePersistenceAndTransport(database.ConnectionString);
                opts.PublishAllMessages().ToSqliteQueue(QueueName);

                if (explicitOrdering == true) expression.EnableDeterministicQueueOrdering();

                // After the transport is registered: the flag has to be read when the transport starts
                if (restore && !restoreFirst) opts.RestoreV6Defaults();

                // Isolates the queue tables in the restart test: the envelope storage of a database first built on
                // the 7.0 layout is a separate question (the 6.x inbox primary key makes SQLite rebuild the table)
                if (keepEnvelopeStorageLayout)
                {
                    opts.Durability.AlwaysProvisionOptionalSchemaObjects = true;
                    opts.Durability.MessageIdentity = MessageIdentity.IdAndDestination;
                }
            }).StartAsync(ct);
    }

    private static SqliteQueue queueOf(IHost host)
    {
        return host.GetRuntime().Options.Transports.GetOrCreate<SqliteTransport>().Queues[QueueName];
    }
}
