using System.Collections.Concurrent;
using IntegrationTests;
using JasperFx.Core;
using Microsoft.Extensions.Hosting;
using Npgsql;
using Shouldly;
using Weasel.Postgresql;
using Wolverine;
using Wolverine.ComplianceTests;
using Wolverine.Postgresql;
using Wolverine.Postgresql.Transport;
using Wolverine.Tracking;

namespace PostgresqlTests.Transport;

/// <summary>
/// GH-5413. After a full batch the listener is meant to poll again after 250ms, but the check was
/// <c>count &gt; MaximumMessagesToReceive</c>, which a pop limited to that many rows can never satisfy. Without a
/// queue notification to wake it -- a backlog left by a crashed node, or the tail after the producer stops --
/// the queue drained one batch per polling interval.
/// </summary>
[Collection("Postgresql")]
public class Bug_5413_a_backlog_drains_without_waiting_a_polling_interval_per_batch
{
    private const string Schema = "gh5413";

    [Fact]
    public async Task a_backlog_drains_without_waiting_for_the_polling_interval()
    {
        Bug5413Handler.Handled.Clear();

        await using (var conn = new NpgsqlConnection(Servers.PostgresConnectionString))
        {
            await conn.OpenAsync(TestContext.Current.CancellationToken);
            await conn.DropSchemaAsync(Schema, TestContext.Current.CancellationToken);
        }

        // The backlog is in the queue table before the listener starts, so no notification ever wakes it
        using (var publisher = await Host.CreateDefaultBuilder()
                   .UseWolverine(opts =>
                   {
                       opts.Discovery.DisableConventionalDiscovery();
                       opts.UsePostgresqlPersistenceAndTransport(Servers.PostgresConnectionString, Schema,
                           transportSchema: Schema).AutoProvision();
                       opts.PublishMessage<Bug5413Message>().ToPostgresqlQueue("gh5413");
                   }).StartAsync(TestContext.Current.CancellationToken))
        {
            var bus = publisher.MessageBus();
            for (var i = 0; i < 100; i++) await bus.PublishAsync(new Bug5413Message(i));

            var queue = publisher.GetRuntime().Options.Transports.GetOrCreate<PostgresqlTransport>().Queues["gh5413"];
            await waitUntilAsync(async () => await queue.CountAsync() == 100, 30.Seconds());

            await publisher.StopAsync(TestContext.Current.CancellationToken);
        }

        using var listener = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Discovery.DisableConventionalDiscovery().IncludeType(typeof(Bug5413Handler));
                opts.UsePostgresqlPersistenceAndTransport(Servers.PostgresConnectionString, Schema,
                    transportSchema: Schema).AutoProvision();

                // 20 per batch by default, and nothing but the full-batch pacing to bring on the next poll
                opts.ListenToPostgresqlQueue("gh5413").PollingInterval(1.Hours());
            }).StartAsync(TestContext.Current.CancellationToken);

        await waitUntilAsync(() => Task.FromResult(Bug5413Handler.Handled.Count == 100), 30.Seconds());

        await listener.StopAsync(TestContext.Current.CancellationToken);
    }

    private static async Task waitUntilAsync(Func<Task<bool>> condition, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow.Add(timeout);
        while (!await condition())
        {
            if (DateTimeOffset.UtcNow > deadline)
            {
                throw new TimeoutException($"Only {Bug5413Handler.Handled.Count} of 100 messages were handled");
            }

            await Task.Delay(100.Milliseconds(), TestContext.Current.CancellationToken);
        }
    }
}

public record Bug5413Message(int Number);

public static class Bug5413Handler
{
    public static readonly ConcurrentDictionary<int, bool> Handled = new();

    public static void Handle(Bug5413Message message)
    {
        Handled[message.Number] = true;
    }
}
