using System.Collections.Concurrent;
using System.Diagnostics;
using IntegrationTests;
using JasperFx.Core;
using Microsoft.Extensions.Hosting;
using Npgsql;
using Shouldly;
using Weasel.Postgresql;
using Wolverine;
using Wolverine.Postgresql;
using Wolverine.Postgresql.Transport;
using Wolverine.Runtime;
using Wolverine.Tracking;
using Wolverine.Util;

namespace PostgresqlTests.Transport;

/// <summary>
/// Manual local benchmark for the PostgreSQL queue notification tradeoff.
/// Run with RUN_QUEUE_NOTIFICATION_BENCHMARK=1 and WOLVERINE_POSTGRES set to a disposable test database.
/// </summary>
[Trait("Category", "Benchmark")]
public class queue_notifications_perf_benchmark
{
    private const int WarmupCount = 30;
    private const int MessageCount = 300;
    private const int SendSamples = 5;
    private const int IdleLatencySamples = 10;
    private static readonly TimeSpan PollingInterval = TimeSpan.FromMilliseconds(750);
    private readonly ITestOutputHelper _output;

    public queue_notifications_perf_benchmark(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public async Task compare_notifications_off_and_on()
    {
        if (Environment.GetEnvironmentVariable("RUN_QUEUE_NOTIFICATION_BENCHMARK") != "1")
        {
            Assert.Skip("Set RUN_QUEUE_NOTIFICATION_BENCHMARK=1 to run this local PostgreSQL benchmark.");
        }

        var off = await runModeAsync(false);
        var on = await runModeAsync(true);

        _output.WriteLine($"PostgreSQL queue notification benchmark (sequential direct sends, median of {SendSamples} batches of {MessageCount})");
        writeResult(off);
        writeResult(on);
        _output.WriteLine($"Send throughput change: {percentChange(off.SendRate, on.SendRate):+0.0;-0.0;0.0}%");
        _output.WriteLine($"End-to-end p50 change: {percentChange(off.P50Milliseconds, on.P50Milliseconds):+0.0;-0.0;0.0}%");
    }

    private static async Task<Measurement> runModeAsync(bool enabled)
    {
        var schema = $"qnb_{Guid.NewGuid():N}"[..12];
        var publisher = await startPublisherAsync(schema, enabled);
        IHost? receiver = null;

        try
        {
            var runtime = publisher.GetRuntime();
            var queue = runtime.Options.Transports.GetOrCreate<PostgresqlTransport>().Queues["bench"];
            var sender = new PostgresqlQueueSender(queue);

            // Measure producer work without a concurrent consumer competing for database resources. Use several
            // batches and report their median to reduce noise from a single short local run.
            await enqueueBatchAsync(sender, queue, runtime, WarmupCount);
            var sendRates = new double[SendSamples];
            for (var i = 0; i < SendSamples; i++)
            {
                sendRates[i] = await enqueueBatchAsync(sender, queue, runtime, MessageCount);
            }

            // Leave the queue empty so this benchmark measures only idle wake latency after producer throughput.
            await using (var cleanup = new NpgsqlConnection(Servers.PostgresConnectionString))
            {
                await cleanup.OpenAsync(TestContext.Current.CancellationToken);
                await cleanup.CreateCommand($"delete from {queue.QueueTable.Identifier}")
                    .ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
            }

            receiver = await startReceiverAsync(schema, enabled);
            if (enabled)
            {
                (await notificationConnectionCountAsync(schema)).ShouldBe(1);
            }

            await Task.Delay(1.Seconds(), TestContext.Current.CancellationToken);
            var latencies = await measureIdleLatencyAsync(sender, queue, runtime, IdleLatencySamples);
            var connectionCount = await notificationConnectionCountAsync(schema);

            var ordered = latencies.Select(x => x.TotalMilliseconds).Order().ToArray();
            var medianSendRate = sendRates.Order().ElementAt(SendSamples / 2);
            return new Measurement(enabled, connectionCount, medianSendRate, ordered[ordered.Length / 2],
                ordered[Math.Min((int)(ordered.Length * 0.95), ordered.Length - 1)]);
        }
        finally
        {
            if (receiver != null)
            {
                await receiver.StopAsync();
                receiver.Dispose();
            }

            await publisher.StopAsync();
            publisher.Dispose();

            await using var conn = new NpgsqlConnection(Servers.PostgresConnectionString);
            await conn.OpenAsync(TestContext.Current.CancellationToken);
            await conn.DropSchemaAsync(schema);
            await conn.DropSchemaAsync($"{schema}_pub");
            await conn.DropSchemaAsync($"{schema}_recv");
        }
    }

    private static async Task<double> enqueueBatchAsync(PostgresqlQueueSender sender, PostgresqlQueue queue,
        WolverineRuntime runtime, int count)
    {
        var serializer = runtime.Options.DefaultSerializer!;
        var sendTimer = Stopwatch.StartNew();

        for (var i = 0; i < count; i++)
        {
            var id = Guid.NewGuid();
            var started = Stopwatch.GetTimestamp();
            var message = new QueueNotificationPerfProbe(id, started);
            var envelope = new Envelope(message)
            {
                Id = id,
                Destination = queue.Uri,
                SentAt = DateTimeOffset.UtcNow,
                ContentType = serializer.ContentType,
                MessageType = typeof(QueueNotificationPerfProbe).ToMessageTypeName()
            };
            envelope.Data = serializer.Write(envelope);

            await sender.SendAsync(envelope, TestContext.Current.CancellationToken);
        }

        sendTimer.Stop();
        return count / sendTimer.Elapsed.TotalSeconds;
    }

    private static async Task<TimeSpan[]> measureIdleLatencyAsync(PostgresqlQueueSender sender,
        PostgresqlQueue queue, WolverineRuntime runtime, int count)
    {
        var serializer = runtime.Options.DefaultSerializer!;
        var latencies = new TimeSpan[count];
        for (var i = 0; i < count; i++)
        {
            // Let the receiver finish handling the prior probe and enter its idle wait.
            await Task.Delay(PollingInterval + TimeSpan.FromMilliseconds(100),
                TestContext.Current.CancellationToken);

            var id = Guid.NewGuid();
            var started = Stopwatch.GetTimestamp();
            var received = QueueNotificationPerfHandler.Register(id);
            var message = new QueueNotificationPerfProbe(id, started);
            var envelope = new Envelope(message)
            {
                Id = id,
                Destination = queue.Uri,
                SentAt = DateTimeOffset.UtcNow,
                ContentType = serializer.ContentType,
                MessageType = typeof(QueueNotificationPerfProbe).ToMessageTypeName()
            };
            envelope.Data = serializer.Write(envelope);
            await sender.SendAsync(envelope, TestContext.Current.CancellationToken);
            latencies[i] = await received.WaitAsync(TimeSpan.FromSeconds(10),
                TestContext.Current.CancellationToken);
        }

        return latencies;
    }

    private static async Task<IHost> startPublisherAsync(string schema, bool enabled)
    {
        return await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Durability.Mode = DurabilityMode.Solo;
                var persistence = opts.UsePostgresqlPersistenceAndTransport(Servers.PostgresConnectionString,
                    schema: $"{schema}_pub", transportSchema: schema).AutoProvision();
                if (enabled) persistence.EnableQueueNotifications();

                opts.PublishMessage<QueueNotificationPerfProbe>().ToPostgresqlQueue("bench");
            }).StartAsync();
    }

    private static async Task<IHost> startReceiverAsync(string schema, bool enabled)
    {
        return await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Durability.Mode = DurabilityMode.Solo;
                opts.Durability.ScheduledJobFirstExecution = 1.Hours();
                var persistence = opts.UsePostgresqlPersistenceAndTransport(Servers.PostgresConnectionString,
                    schema: $"{schema}_recv", transportSchema: schema).AutoProvision();
                if (enabled) persistence.EnableQueueNotifications();

                opts.ListenToPostgresqlQueue("bench").PollingInterval(PollingInterval);
            }).StartAsync();
    }

    private static async Task<int> notificationConnectionCountAsync(string schema)
    {
        await using var conn = new NpgsqlConnection(Servers.PostgresConnectionString);
        await conn.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = conn.CreateCommand(
            "select count(*) from pg_stat_activity where datname = current_database() and application_name = @name");
        command.Parameters.AddWithValue("name", PostgresqlQueueNotification.ApplicationName(schema));
        return Convert.ToInt32(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken));
    }

    private void writeResult(Measurement result)
    {
        _output.WriteLine(
            $"notifications {(result.Enabled ? "on" : "off")}: {result.SendRate:N0} sends/s, " +
            $"idle delivery p50 {result.P50Milliseconds:N1} ms / p95 {result.P95Milliseconds:N1} ms, " +
            $"notification sessions {result.NotificationConnections}");
    }

    private static double percentChange(double before, double after) => (after - before) / before * 100;

    private sealed record Measurement(bool Enabled, int NotificationConnections, double SendRate,
        double P50Milliseconds, double P95Milliseconds);
}

public record QueueNotificationPerfProbe(Guid Id, long StartedTimestamp);

public static class QueueNotificationPerfHandler
{
    private static readonly ConcurrentDictionary<Guid, TaskCompletionSource<TimeSpan>> Pending = new();

    public static Task<TimeSpan> Register(Guid id)
    {
        var source = new TaskCompletionSource<TimeSpan>(TaskCreationOptions.RunContinuationsAsynchronously);
        Pending[id] = source;
        return source.Task;
    }

    public static void Handle(QueueNotificationPerfProbe message)
    {
        if (Pending.TryRemove(message.Id, out var source))
        {
            source.TrySetResult(Stopwatch.GetElapsedTime(message.StartedTimestamp));
        }
    }
}
