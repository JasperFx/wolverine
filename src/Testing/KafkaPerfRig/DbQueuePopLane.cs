using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using IntegrationTests;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Wolverine;
using Wolverine.Postgresql;
using Wolverine.Postgresql.Transport;
using Wolverine.Runtime;
using Wolverine.SqlServer;
using Wolverine.SqlServer.Transport;
using Wolverine.Tracking;
using Wolverine.Transports;

namespace KafkaPerfRig;

/// <summary>
/// GH-5015 follow-up. A direct timing of the database queue transports' durable pop, which #5020 and #5021
/// moved from an implicit transaction (PostgreSQL) and auto-committed statements (SQL Server) to an explicit
/// transaction that commits after the batch is read -- one more round trip per poll.
///
/// <para>
/// Deliberately NOT an end-to-end cell, for the reason <see cref="OutboxStoreLane" /> gives: the change is one
/// round trip on one method, and broker or handler time would bury it. The before/after comparison is two
/// independently built worktrees running this same file, interleaved, because the change is committed and
/// there is no in-build switch for it.
/// </para>
///
/// <para>
/// Three cells per round:
/// <list type="bullet">
/// <item><c>empty_pop_ms</c> -- one pop against an empty queue. Most polls on most queues are this shape,
/// and it is where one extra round trip is the largest fraction of the work.</item>
/// <item><c>batch_pop_ms</c> -- one pop of a full batch (<c>MaximumMessagesToReceive</c>, 20 by default).</item>
/// <item><c>drain_msgs_per_s</c> -- pop a <c>RIG_DRAIN_MESSAGES</c> backlog to empty, batch by batch. The
/// listener's pop ceiling, with no handler in the way.</item>
/// </list>
/// </para>
///
/// <para><c>RIG_STORE</c>: postgresql (default) | sqlserver.</para>
/// </summary>
public static class DbQueuePopLane
{
    private const string QueueName = "rig_pop";

    public static async Task RunAsync(RigConfig cfg)
    {
        var store = Environment.GetEnvironmentVariable("RIG_STORE") ?? "postgresql";
        var rounds = envInt("RIG_ROUNDS", 20);
        var warmup = envInt("RIG_WARMUP_ROUNDS", 5);
        var emptyPops = envInt("RIG_EMPTY_POPS", 200);
        var drainMessages = envInt("RIG_DRAIN_MESSAGES", 5000);
        var schema = Environment.GetEnvironmentVariable("RIG_POP_SCHEMA") ?? "rig_pop";

        using var host = await Host.CreateDefaultBuilder()
            .ConfigureLogging(logging => logging.SetMinimumLevel(LogLevel.Warning))
            .UseWolverine(opts =>
            {
                opts.Durability.Mode = DurabilityMode.Solo;
                opts.ApplicationAssembly = typeof(LocalRigHandlers).Assembly;

                // Declare the queue without a listener: the lane pops it directly
                if (store == "sqlserver")
                {
                    opts.UseSqlServerPersistenceAndTransport(Servers.SqlServerConnectionString, schema)
                        .AutoProvision();
                    opts.PublishMessage<PopProbe>().ToSqlServerQueue(QueueName);
                }
                else
                {
                    opts.UsePostgresqlPersistenceAndTransport(Servers.PostgresConnectionString, schema,
                        transportSchema: schema);
                    opts.PublishMessage<PopProbe>().ToPostgresqlQueue(QueueName);
                }
            }).StartAsync();

        var runtime = host.GetRuntime();
        var popper = store == "sqlserver" ? sqlServerPopper(runtime) : postgresqlPopper(runtime);
        var batchSize = popper.BatchSize;

        Console.WriteLine(
            $"[rig] db-queue-pop: store={store} batch={batchSize} rounds={rounds} (+{warmup} warmup) emptyPops={emptyPops} drain={drainMessages}");

        await popper.ResetAsync();

        var emptyPop = new List<double>();
        var batchPop = new List<double>();
        var drainRate = new List<double>();

        for (var round = 0; round < rounds + warmup; round++)
        {
            var measured = round >= warmup;

            // Empty pops: the queue and inbox start empty every round
            for (var i = 0; i < emptyPops; i++)
            {
                var start = Stopwatch.GetTimestamp();
                var popped = await popper.PopAsync(batchSize);
                var elapsed = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
                if (popped != 0) throw new InvalidOperationException("The queue was expected to be empty");
                if (measured) emptyPop.Add(elapsed);
            }

            // One full batch
            await popper.FillAsync(batchSize);
            {
                var start = Stopwatch.GetTimestamp();
                var popped = await popper.PopAsync(batchSize);
                var elapsed = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
                if (popped != batchSize) throw new InvalidOperationException($"Popped {popped}, expected {batchSize}");
                if (measured) batchPop.Add(elapsed);
            }
            await popper.ResetAsync();

            // Drain a backlog
            await popper.FillAsync(drainMessages);
            {
                var start = Stopwatch.GetTimestamp();
                var total = 0;
                while (total < drainMessages)
                {
                    var popped = await popper.PopAsync(batchSize);
                    if (popped == 0) throw new InvalidOperationException($"Queue ran dry at {total} of {drainMessages}");
                    total += popped;
                }

                var seconds = Stopwatch.GetElapsedTime(start).TotalSeconds;
                if (measured) drainRate.Add(drainMessages / seconds);
            }

            // A growing inbox is a confound that would flatter whichever run went first
            await popper.ResetAsync();
        }

        var summary = new Dictionary<string, object>
        {
            ["store"] = store,
            ["batchSize"] = batchSize,
            ["rounds"] = rounds,
            ["empty_pop_ms"] = describe(emptyPop),
            ["batch_pop_ms"] = describe(batchPop),
            ["drain_msgs_per_s"] = describe(drainRate)
        };

        Directory.CreateDirectory(cfg.OutDir);
        var json = JsonSerializer.Serialize(summary, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(Path.Combine(cfg.OutDir, $"db-queue-pop-{store}.json"), json);
        Console.WriteLine(json);

        await host.StopAsync();
    }

    private sealed record Popper(int BatchSize, Func<int, Task<int>> PopAsync, Func<int, Task> FillAsync, Func<Task> ResetAsync);

    private static Popper sqlServerPopper(IWolverineRuntime runtime)
    {
        var transport = runtime.Options.Transports.OfType<SqlServerTransport>().Single();
        var queue = transport.Queues[QueueName];
        var store = runtime.Storage;

        return new Popper(
            queue.MaximumMessagesToReceive,
            async count => (await queue.TryPopDurablyAsync(count, runtime.DurabilitySettings, NullLogger.Instance,
                CancellationToken.None)).Count,
            count => fillAsync(count, envelope => queue.SendAsync(envelope)),
            async () =>
            {
                await queue.PurgeAsync(NullLogger.Instance);
                await store.Admin.ClearAllAsync();
            });
    }

    /// <summary>
    /// The PostgreSQL pop lives only on the internal listener, so it is built and called through reflection.
    /// Its constructor -- queue, runtime, receiver, data source, database name -- is the same on both sides of
    /// the change, and the receiver is only stored, never called by a pop.
    /// </summary>
    private static Popper postgresqlPopper(IWolverineRuntime runtime)
    {
        var transport = runtime.Options.Transports.GetOrCreate<PostgresqlTransport>();
        var queue = transport.Queues[QueueName];
        var store = runtime.Storage;

        var dataSource = typeof(PostgresqlQueue)
            .GetProperty("DataSource", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(queue)!;

        var listenerType = typeof(PostgresqlQueue).Assembly
            .GetType("Wolverine.Postgresql.Transport.PostgresqlQueueListener", throwOnError: true)!;
        var listener = Activator.CreateInstance(listenerType,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null,
            [queue, runtime, DispatchProxy.Create<IReceiver, InertProxy>(), dataSource, null], null)!;
        var pop = listenerType.GetMethod("TryPopDurablyAsync")!;

        return new Popper(
            queue.MaximumMessagesToReceive,
            async count =>
            {
                var task = (Task<IReadOnlyList<Envelope>>)pop.Invoke(listener,
                    [count, runtime.DurabilitySettings, NullLogger.Instance, CancellationToken.None])!;
                return (await task).Count;
            },
            count => fillAsync(count, envelope => queue.SendAsync(envelope)),
            async () =>
            {
                await queue.PurgeAsync(NullLogger.Instance);
                await store.Admin.ClearAllAsync();
            });
    }

    private static async Task fillAsync(int count, Func<Envelope, ValueTask> send)
    {
        var data = System.Text.Encoding.UTF8.GetBytes(Payloads.Small);
        for (var i = 0; i < count; i++)
        {
            await send(new Envelope
            {
                Id = Guid.NewGuid(),
                MessageType = "rig.pop.message",
                ContentType = "application/json",
                Data = data
            });
        }
    }

    private static Dictionary<string, double> describe(List<double> values)
    {
        if (values.Count == 0) return new Dictionary<string, double>();
        return new Dictionary<string, double>
        {
            ["mean"] = Math.Round(values.Average(), 3),
            ["p50"] = Math.Round(percentile(values, 50), 3),
            ["p95"] = Math.Round(percentile(values, 95), 3),
            ["min"] = Math.Round(values.Min(), 3),
            ["max"] = Math.Round(values.Max(), 3),
            ["n"] = values.Count
        };
    }

    private static double percentile(List<double> values, double p)
    {
        var sorted = values.OrderBy(x => x).ToArray();
        var index = (int)Math.Ceiling(p / 100 * sorted.Length) - 1;
        return sorted[Math.Clamp(index, 0, sorted.Length - 1)];
    }

    private static int envInt(string name, int fallback)
        => int.TryParse(Environment.GetEnvironmentVariable(name), out var value) ? value : fallback;
}

public record PopProbe;

/// <summary>
/// Stands in for the listener's receiver, which a pop never calls
/// </summary>
public class InertProxy : DispatchProxy
{
    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        var returnType = targetMethod?.ReturnType;
        if (returnType == typeof(Task)) return Task.CompletedTask;
        if (returnType == typeof(ValueTask)) return ValueTask.CompletedTask;
        return returnType is { IsValueType: true } ? Activator.CreateInstance(returnType) : null;
    }
}
