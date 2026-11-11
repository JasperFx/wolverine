using System.Collections.Concurrent;
using IntegrationTests;
using JasperFx.Core;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Wolverine;
using Wolverine.ComplianceTests;
using Wolverine.Oracle;
using Wolverine.Oracle.Transport;
using Wolverine.Tracking;

namespace OracleTests.Transport;

/// <summary>
/// GH-5413. After a full batch the listener is meant to poll again after 250ms, but the check was
/// <c>count &gt; MaximumMessagesToReceive</c>, which a pop limited to that many rows can never satisfy. Without a
/// queue notification to wake it -- a backlog left by a crashed node, or the tail after the producer stops --
/// the queue drained one batch per polling interval.
/// </summary>
[Collection("oracle")]
public class Bug_5413_a_backlog_drains_without_waiting_a_polling_interval_per_batch
{
    [Fact]
    public async Task a_backlog_drains_without_waiting_for_the_polling_interval()
    {
        Bug5413Handler.Handled.Clear();

        // The backlog is in the queue table before the listener starts, so no notification ever wakes it
        using (var publisher = await Host.CreateDefaultBuilder()
                   .UseWolverine(opts =>
                   {
                       opts.Discovery.DisableConventionalDiscovery();
                       opts.PersistMessagesWithOracle(Servers.OracleConnectionString, "WOLVERINE").EnableMessageTransport();
                       opts.Durability.Mode = DurabilityMode.Solo;
                       opts.PublishMessage<Bug5413Message>().ToOracleQueue("gh5413");
                   }).StartAsync(TestContext.Current.CancellationToken))
        {
            // Oracle tests share one schema, so start from an empty queue
            var queue = publisher.GetRuntime().Options.Transports.GetOrCreate<OracleTransport>().Queues["gh5413"];
            await queue.PurgeAsync(NullLogger.Instance);

            var bus = publisher.MessageBus();
            for (var i = 0; i < 100; i++) await bus.PublishAsync(new Bug5413Message(i));

            await waitUntilAsync(async () => await queue.CountAsync() == 100, 30.Seconds());

            await publisher.StopAsync(TestContext.Current.CancellationToken);
        }

        using var listener = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Discovery.DisableConventionalDiscovery().IncludeType(typeof(Bug5413Handler));
                opts.PersistMessagesWithOracle(Servers.OracleConnectionString, "WOLVERINE").EnableMessageTransport();
                opts.Durability.Mode = DurabilityMode.Solo;

                // 20 per batch by default, and nothing but the full-batch pacing to bring on the next poll
                opts.ListenToOracleQueue("gh5413").PollingInterval(1.Hours());
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
