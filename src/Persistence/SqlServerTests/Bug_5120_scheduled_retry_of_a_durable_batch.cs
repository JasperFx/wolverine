using IntegrationTests;
using JasperFx.Core;
using JasperFx.Resources;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine;
using Wolverine.ErrorHandling;
using Wolverine.SqlServer;

namespace SqlServerTests;

public record Bug5120Item(Guid Id);

public class Bug5120Exception : Exception;

public static class Bug5120ItemHandler
{
    private static int _executions;
    private static int _failures;

    public static int Executions => _executions;

    public static void Reset(int failures)
    {
        _executions = 0;
        _failures = failures;
    }

    // Only an array handler, no scalar handler: the batch is the unit of work
    public static void Handle(Bug5120Item[] items)
    {
        if (Interlocked.Increment(ref _executions) <= _failures)
        {
            throw new Bug5120Exception();
        }
    }
}

/// <summary>
/// GH-5120. A native batch on a durable local queue fails and its error policy is ScheduleRetry. The
/// durable receiver reschedules by UPDATE-ing the inbox row for the envelope's id -- but the envelope is
/// the assembled batch, which has no row. Only its members do. The update matches nothing, the retry
/// never runs, and the members sit 'Incoming', owned by the live node, forever.
/// </summary>
public class Bug_5120_scheduled_retry_of_a_durable_batch
{
    private const string Schema = "bug_5120";

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task a_failed_durable_batch_is_retried_on_its_schedule_and_its_members_settle()
    {
        Bug5120ItemHandler.Reset(failures: 1);

        using var host = await startHostAsync();

        var bus = host.MessageBus();
        await bus.SendAsync(new Bug5120Item(Guid.NewGuid()));
        await bus.SendAsync(new Bug5120Item(Guid.NewGuid()));

        // The first execution fails; the scheduled retry should follow within a few hundred ms
        await waitForExecutionsAsync(2);
        Bug5120ItemHandler.Executions.ShouldBe(2);

        // And once the retry succeeds, the members' inbox rows are settled
        (await waitForMembersToSettleAsync()).ShouldBeEmpty();
    }

    [Fact]
    public async Task a_durable_batch_that_keeps_failing_exhausts_the_policy_instead_of_retrying_forever()
    {
        // The carrier is rebuilt from scratch whenever members are re-batched, so its Attempts only reaches the
        // policy's limit if the SAME carrier is retried
        Bug5120ItemHandler.Reset(failures: int.MaxValue);

        using var host = await startHostAsync();

        var bus = host.MessageBus();
        await bus.SendAsync(new Bug5120Item(Guid.NewGuid()));
        await bus.SendAsync(new Bug5120Item(Guid.NewGuid()));

        // The first execution plus the policy's three scheduled retries, then dead lettered
        await waitForExecutionsAsync(4);
        (await waitForMembersToSettleAsync()).ShouldBeEmpty();

        // Long enough for any further retry to have shown up
        await Task.Delay(1.Seconds(), Token);
        Bug5120ItemHandler.Executions.ShouldBe(4);
    }

    private static async Task<IHost> startHostAsync()
    {
        var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.PersistMessagesWithSqlServer(Servers.SqlServerConnectionString, Schema);
                opts.Durability.Mode = DurabilityMode.Solo;
                opts.Durability.ScheduledJobFirstExecution = 10.Milliseconds();
                opts.Durability.ScheduledJobPollingTime = 25.Milliseconds();

                opts.LocalQueueFor<Bug5120Item>().UseDurableInbox();

                opts.BatchMessagesOf<Bug5120Item>(batching =>
                    {
                        batching.BatchSize = 100;
                        batching.TriggerTime = 250.Milliseconds();
                    })
                    .UseDurableInbox()
                    .Sequential();

                opts.Policies.OnException<Bug5120Exception>()
                    .ScheduleRetry(50.Milliseconds(), 150.Milliseconds(), 300.Milliseconds());

                opts.Discovery.DisableConventionalDiscovery().IncludeType(typeof(Bug5120ItemHandler));
            }).StartAsync(Token);

        await host.ResetResourceState(Token);

        return host;
    }

    private static async Task waitForExecutionsAsync(int expected)
    {
        var deadline = DateTimeOffset.UtcNow.Add(15.Seconds());
        while (Bug5120ItemHandler.Executions < expected && DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(100.Milliseconds(), Token);
        }

        Bug5120ItemHandler.Executions.ShouldBeGreaterThanOrEqualTo(expected);
    }

    private static async Task<List<string>> waitForMembersToSettleAsync()
    {
        var unsettled = await unsettledRowsAsync();
        var deadline = DateTimeOffset.UtcNow.Add(5.Seconds());
        while (unsettled.Any() && DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(100.Milliseconds(), Token);
            unsettled = await unsettledRowsAsync();
        }

        return unsettled;
    }

    private static async Task<List<string>> unsettledRowsAsync()
    {
        var unsettled = new List<string>();

        await using var conn = new SqlConnection(Servers.SqlServerConnectionString);
        await conn.OpenAsync(Token);

        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            $"select id, status, owner_id, attempts from {Schema}.wolverine_incoming_envelopes where status <> 'Handled'";

        await using var reader = await cmd.ExecuteReaderAsync(Token);
        while (await reader.ReadAsync(Token))
        {
            unsettled.Add($"{reader.GetGuid(0)} status={reader.GetString(1)} owner_id={reader.GetInt32(2)} attempts={reader.GetInt32(3)}");
        }

        return unsettled;
    }
}
