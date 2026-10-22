using IntegrationTests;
using JasperFx.Core;
using JasperFx.Resources;
using Microsoft.Extensions.Hosting;
using Npgsql;
using Shouldly;
using Wolverine;
using Wolverine.Postgresql;

namespace PostgresqlTests.Bugs;

public record SettlingItem(Guid Id, bool Poison);

public static class SettlingItemBatchHandler
{
    private static int _executions;
    private static int _poisonThrows;

    public static int Executions => _executions;
    public static int PoisonThrows => _poisonThrows;

    public static void Reset()
    {
        _executions = 0;
        _poisonThrows = 0;
    }

    public static void Handle(SettlingItem[] items)
    {
        Interlocked.Increment(ref _executions);

        var poison = items.FirstOrDefault(x => x.Poison);
        if (poison != null)
        {
            Interlocked.Increment(ref _poisonThrows);
            throw ApplyItemException.DeadLetterAndReplayOthers(poison);
        }
    }
}

/// <summary>
/// Durable compliance findings F1, F3 and B1. Items arrive on a durable local queue, so every one of them has its own
/// inbox row, and are batched by <c>BatchMessagesOf</c>. Each member has to be settled as itself: when its batch
/// finishes, and -- for a survivor of an <see cref="ApplyItemException"/> -- when its replay finishes.
/// <para>
/// F3: on the DEFAULT, buffered, batch queue the batch's terminal settled nothing, so every member stayed Incoming and
/// ran again after the next restart. F1/B1: the survivors were replayed as new envelopes with no Destination or row
/// (marking them handled failed forever) while their own rows were marked Handled before the replay ran.
/// </para>
/// </summary>
public class durable_batch_members_settle_as_themselves
{
    private const string Schema = "batch_members";

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task every_member_of_a_successful_batch_is_marked_handled(bool durableBatchQueue)
    {
        SettlingItemBatchHandler.Reset();
        using var host = await startHostAsync(durableBatchQueue);

        await sendAsync(host, 5);
        await waitForAsync(() => SettlingItemBatchHandler.Executions >= 1);

        // On the buffered batch queue every row was left Incoming, to run again after the next restart
        var statuses = await waitForStatusesAsync(5);
        statuses.ShouldAllBe(x => x == "Handled");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task an_apply_item_poison_is_dead_lettered_and_the_replayed_survivors_are_marked_handled(
        bool durableBatchQueue)
    {
        SettlingItemBatchHandler.Reset();
        using var host = await startHostAsync(durableBatchQueue);

        await sendAsync(host, 4);
        await host.MessageBus().SendAsync(new SettlingItem(Guid.NewGuid(), true));

        // The poisoned batch, then the replay of the survivors
        await waitForAsync(() => SettlingItemBatchHandler.PoisonThrows >= 1 && SettlingItemBatchHandler.Executions >= 2);

        // The four survivors settled by their replay; the poison's row went with its move to the dead letters
        var statuses = await waitForStatusesAsync(4);
        statuses.ShouldAllBe(x => x == "Handled");

        (await deadLetterCountAsync()).ShouldBe(1);
    }

    private static async Task<IHost> startHostAsync(bool durableBatchQueue)
    {
        var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.PersistMessagesWithPostgresql(Servers.PostgresConnectionString, Schema);
                opts.Durability.Mode = DurabilityMode.Solo;

                // The items arrive on one durable queue, so each has its own inbox row...
                opts.PublishMessage<SettlingItem>().ToLocalQueue("settling-items").UseDurableInbox();

                // ...and are batched on another, buffered unless made durable
                var batchQueue = opts.BatchMessagesOf<SettlingItem>(batching =>
                {
                    batching.BatchSize = 100;
                    batching.TriggerTime = 250.Milliseconds();
                    batching.LocalExecutionQueueName = "settling-batches";
                }).Sequential();

                if (durableBatchQueue)
                {
                    batchQueue.UseDurableInbox();
                }

                opts.Discovery.DisableConventionalDiscovery().IncludeType(typeof(SettlingItemBatchHandler));
            }).StartAsync(Token);

        await host.ResetResourceState(Token);

        return host;
    }

    private static async Task sendAsync(IHost host, int count)
    {
        var ids = Enumerable.Range(0, count).Select(_ => Guid.NewGuid()).ToArray();
        var bus = host.MessageBus();
        foreach (var id in ids)
        {
            await bus.SendAsync(new SettlingItem(id, false));
        }
    }

    private static async Task waitForAsync(Func<bool> condition)
    {
        await waitForAsync(() => Task.FromResult(condition()));
        condition().ShouldBeTrue();
    }

    private static async Task<bool> waitForAsync(Func<Task<bool>> condition)
    {
        var deadline = DateTimeOffset.UtcNow.Add(15.Seconds());
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (await condition()) return true;
            await Task.Delay(100.Milliseconds(), Token);
        }

        return await condition();
    }

    private static async Task<List<string>> waitForStatusesAsync(int expected)
    {
        var statuses = await statusesAsync();
        await waitForAsync(async () =>
        {
            statuses = await statusesAsync();
            return statuses.Count == expected && statuses.All(x => x == "Handled");
        });

        statuses.Count.ShouldBe(expected);
        return statuses;
    }

    private static async Task<List<string>> statusesAsync()
    {
        await using var conn = new NpgsqlConnection(Servers.PostgresConnectionString);
        await conn.OpenAsync(Token);

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"select status from {Schema}.wolverine_incoming_envelopes";

        var statuses = new List<string>();
        await using var reader = await cmd.ExecuteReaderAsync(Token);
        while (await reader.ReadAsync(Token))
        {
            statuses.Add(reader.GetString(0));
        }

        return statuses;
    }

    private static async Task<long> deadLetterCountAsync()
    {
        await using var conn = new NpgsqlConnection(Servers.PostgresConnectionString);
        await conn.OpenAsync(Token);

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"select count(*) from {Schema}.wolverine_dead_letters";

        return (long)(await cmd.ExecuteScalarAsync(Token))!;
    }
}
