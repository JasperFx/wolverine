using System.Collections.Concurrent;
using System.Diagnostics;
using IntegrationTests;
using JasperFx.Core;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Shouldly;
using Weasel.Postgresql;
using Wolverine;
using Wolverine.Postgresql;
using Wolverine.Postgresql.Transport;
using Wolverine.Runtime.Serialization;
using Wolverine.Util;
using Wolverine.Tracking;

namespace PostgresqlTests.Transport;

public class queue_notifications : PostgresqlContext
{
    [Fact]
    public async Task opt_in_wakes_an_idle_listener_and_shares_one_notification_connection_across_queues()
    {
        var schema = $"qnotify_{Guid.NewGuid():N}"[..16];
        var (publisher, receiver) = await startHostsAsync(schema, true, TimeSpan.FromHours(1));

        try
        {
            // Listener startup waits until LISTEN is active, so a notification sent immediately after host startup
            // cannot be lost before the dedicated session subscribes.
            (await notificationConnectionCountAsync(schema)).ShouldBe(1);

            var stopwatch = Stopwatch.StartNew();
            var session = await publisher.TrackActivity()
                .AlsoTrack(receiver)
                .Timeout(5.Seconds())
                .PublishMessageAndWaitAsync(new QueueNotificationProbe(Guid.NewGuid()));
            stopwatch.Stop();

            stopwatch.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(5));
            session.Received.RecordsInOrder().Any(x => x.Message is QueueNotificationProbe).ShouldBeTrue();
        }
        finally
        {
            await stopAndCleanAsync(publisher, receiver, schema);
        }
    }

    [Fact]
    public async Task notifications_are_off_by_default_and_polling_still_delivers_messages()
    {
        var schema = $"qnotify_{Guid.NewGuid():N}"[..16];
        var (publisher, receiver) = await startHostsAsync(schema, false, TimeSpan.FromMilliseconds(250));

        try
        {
            (await notificationConnectionCountAsync(schema)).ShouldBe(0);

            var session = await publisher.TrackActivity()
                .AlsoTrack(receiver)
                .Timeout(10.Seconds())
                .PublishMessageAndWaitAsync(new QueueNotificationProbe(Guid.NewGuid()));

            session.Received.RecordsInOrder().Any(x => x.Message is QueueNotificationProbe).ShouldBeTrue();
            (await notificationConnectionCountAsync(schema)).ShouldBe(0);
        }
        finally
        {
            await stopAndCleanAsync(publisher, receiver, schema);
        }
    }

    [Fact]
    public async Task notifications_reject_multiplexed_data_sources()
    {
        var connectionString = new NpgsqlConnectionStringBuilder(Servers.PostgresConnectionString)
        {
            Multiplexing = true
        };
        await using var dataSource = NpgsqlDataSource.Create(connectionString);
        var manager = new PostgresqlQueueNotificationManager();
        IAsyncDisposable? registration = null;

        try
        {
            var exception = await Should.ThrowAsync<InvalidOperationException>(async () =>
            {
                registration = await manager.RegisterAsync(dataSource, "default", "queue_schema.messages",
                    static () => { }, NullLogger.Instance, TestContext.Current.CancellationToken);
            });

            exception.Message.ShouldContain("Multiplexing=false");
        }
        finally
        {
            if (registration != null)
            {
                await registration.DisposeAsync();
            }
        }
    }

    [Fact]
    public async Task polling_delivers_after_notification_connection_drops_and_listener_reconnects()
    {
        var schema = $"qnotify_{Guid.NewGuid():N}"[..16];
        var (publisher, receiver) = await startHostsAsync(schema, true, TimeSpan.FromMilliseconds(250));

        try
        {
            (await notificationConnectionCountAsync(schema)).ShouldBe(1);
            await terminateNotificationConnectionAsync(schema);

            var runtime = receiver.GetRuntime();
            var queue = runtime.Options.Transports.GetOrCreate<PostgresqlTransport>().Queues["messages"];
            var serializer = runtime.Options.DefaultSerializer!;
            var id = Guid.NewGuid();
            var envelope = new Envelope(new QueueNotificationProbe(id))
            {
                Id = id,
                Destination = queue.Uri,
                SentAt = DateTimeOffset.UtcNow,
                ContentType = serializer.ContentType,
                MessageType = typeof(QueueNotificationProbe).ToMessageTypeName()
            };
            envelope.Data = serializer.Write(envelope);

            var received = QueueNotificationProbeHandler.WaitForAsync(id, 5.Seconds());
            await insertQueueRowWithoutNotificationAsync(queue, envelope);
            await received;
            await waitForNotificationConnectionAsync(schema, 5.Seconds());

            (await notificationConnectionCountAsync(schema)).ShouldBe(1);
        }
        finally
        {
            await stopAndCleanAsync(publisher, receiver, schema);
        }
    }

    [Fact]
    public async Task direct_queue_sends_wake_an_idle_listener()
    {
        var schema = $"qnotify_{Guid.NewGuid():N}"[..16];
        var (publisher, receiver) = await startHostsAsync(schema, true, TimeSpan.FromHours(1));

        try
        {
            var runtime = publisher.GetRuntime();
            var queue = runtime.Options.Transports.GetOrCreate<PostgresqlTransport>().Queues["messages"];
            var serializer = runtime.Options.DefaultSerializer!;
            var sender = new PostgresqlQueueSender(queue);

            for (var i = 0; i < 2; i++)
            {
                var id = Guid.NewGuid();
                var envelope = new Envelope(new QueueNotificationProbe(id))
                {
                    Id = id,
                    Destination = queue.Uri,
                    SentAt = DateTimeOffset.UtcNow,
                    ContentType = serializer.ContentType,
                    MessageType = typeof(QueueNotificationProbe).ToMessageTypeName()
                };
                envelope.Data = serializer.Write(envelope);

                var received = QueueNotificationProbeHandler.WaitForAsync(id, 5.Seconds());
                var stopwatch = Stopwatch.StartNew();
                await sender.SendAsync(envelope, TestContext.Current.CancellationToken);
                await received;
                stopwatch.Stop();
                stopwatch.Elapsed.ShouldBeLessThan(5.Seconds());
            }
        }
        finally
        {
            await stopAndCleanAsync(publisher, receiver, schema);
        }
    }

    [Fact]
    public async Task scheduled_message_promotion_wakes_an_idle_listener()
    {
        var schema = $"qnotify_{Guid.NewGuid():N}"[..16];
        var (publisher, receiver) = await startHostsAsync(schema, true, TimeSpan.FromHours(1), 100.Milliseconds());
        var id = Guid.NewGuid();

        try
        {
            var runtime = publisher.GetRuntime();
            var transport = runtime.Options.Transports.GetOrCreate<PostgresqlTransport>();
            var queue = transport.Queues["messages"];
            var serializer = runtime.Options.DefaultSerializer!;
            var message = new QueueNotificationProbe(id);
            var envelope = new Envelope(message)
            {
                Id = id,
                Destination = queue.Uri,
                Status = EnvelopeStatus.Scheduled,
                ScheduledTime = DateTimeOffset.UtcNow.Subtract(1.Seconds()),
                SentAt = DateTimeOffset.UtcNow,
                ContentType = serializer.ContentType,
                MessageType = typeof(QueueNotificationProbe).ToMessageTypeName()
            };
            envelope.Data = serializer.Write(envelope);

            var received = QueueNotificationProbeHandler.WaitForAsync(id, 10.Seconds());
            await new PostgresqlQueueSender(queue).ScheduleMessageAsync(envelope,
                TestContext.Current.CancellationToken);

            var stopwatch = Stopwatch.StartNew();
            await received;
            stopwatch.Stop();
            stopwatch.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(10));
        }
        finally
        {
            await stopAndCleanAsync(publisher, receiver, schema);
        }
    }

    private static async Task<(IHost Publisher, IHost Receiver)> startHostsAsync(string schema,
        bool enableNotifications, TimeSpan pollingInterval, TimeSpan? scheduledFirstExecution = null)
    {
        var publisher = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Durability.Mode = DurabilityMode.Solo;
                if (scheduledFirstExecution.HasValue)
                {
                    opts.Durability.ScheduledJobFirstExecution = scheduledFirstExecution.Value;
                }

                var persistence = opts.UsePostgresqlPersistenceAndTransport(Servers.PostgresConnectionString,
                    schema: $"{schema}_publisher", transportSchema: schema).AutoProvision();
                if (enableNotifications) persistence.EnableQueueNotifications();

                opts.PublishMessage<QueueNotificationProbe>().ToPostgresqlQueue("messages");
            }).StartAsync();

        var receiver = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Durability.Mode = DurabilityMode.Solo;
                var persistence = opts.UsePostgresqlPersistenceAndTransport(Servers.PostgresConnectionString,
                    schema: $"{schema}_receiver", transportSchema: schema).AutoProvision();
                if (enableNotifications) persistence.EnableQueueNotifications();

                opts.ListenToPostgresqlQueue("messages").PollingInterval(pollingInterval);
                opts.ListenToPostgresqlQueue("other").PollingInterval(pollingInterval);
            }).StartAsync();

        return (publisher, receiver);
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

    private static async Task terminateNotificationConnectionAsync(string schema)
    {
        await using var conn = new NpgsqlConnection(Servers.PostgresConnectionString);
        await conn.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = conn.CreateCommand(
            "select pg_terminate_backend(pid) from pg_stat_activity where datname = current_database() and application_name = @name");
        command.Parameters.AddWithValue("name", PostgresqlQueueNotification.ApplicationName(schema));
        (await command.ExecuteScalarAsync(TestContext.Current.CancellationToken)).ShouldBe(true);
    }

    private static async Task insertQueueRowWithoutNotificationAsync(PostgresqlQueue queue, Envelope envelope)
    {
        await using var conn = new NpgsqlConnection(Servers.PostgresConnectionString);
        await conn.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = conn.CreateCommand();
        command.CommandText =
            $"insert into {queue.QueueTable.Identifier} (id, body, message_type) values (@id, @body, @type)";
        command.Parameters.AddWithValue("id", envelope.Id);
        command.Parameters.AddWithValue("body", EnvelopeSerializer.Serialize(envelope));
        command.Parameters.AddWithValue("type", envelope.MessageType!);
        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }

    private static async Task waitForNotificationConnectionAsync(string schema, TimeSpan timeout)
    {
        var timer = Stopwatch.StartNew();
        while (timer.Elapsed < timeout)
        {
            if (await notificationConnectionCountAsync(schema) == 1) return;
            await Task.Delay(50, TestContext.Current.CancellationToken);
        }

        (await notificationConnectionCountAsync(schema)).ShouldBe(1);
    }

    private static async Task stopAndCleanAsync(IHost publisher, IHost receiver, string schema)
    {
        await publisher.StopAsync();
        await receiver.StopAsync();
        publisher.Dispose();
        receiver.Dispose();

        await using var conn = new NpgsqlConnection(Servers.PostgresConnectionString);
        await conn.OpenAsync(TestContext.Current.CancellationToken);
        await conn.DropSchemaAsync(schema);
        await conn.DropSchemaAsync($"{schema}_publisher");
        await conn.DropSchemaAsync($"{schema}_receiver");
    }
}

public record QueueNotificationProbe(Guid Id);

public static class QueueNotificationProbeHandler
{
    private static readonly ConcurrentDictionary<Guid, TaskCompletionSource> Pending = new();

    public static Task WaitForAsync(Guid id, TimeSpan timeout)
    {
        var source = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Pending[id] = source;
        return source.Task.WaitAsync(timeout, TestContext.Current.CancellationToken);
    }

    public static void Handle(QueueNotificationProbe message)
    {
        if (Pending.TryRemove(message.Id, out var source)) source.TrySetResult();
    }
}
