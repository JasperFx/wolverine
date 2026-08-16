using System.Net;
using System.Text.Json;
using JasperFx.Core;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine.RabbitMQ.Internal;
using Wolverine.Tracking;
using Xunit;

namespace Wolverine.RabbitMQ.Tests;

/// <summary>
/// GH-3950, suggestion 2: turn "Wolverine appears to recover" into an assertion.
///
/// <para>
/// The issue documents a RabbitMQ.Client defect where one rejected delivery tag on a busy channel
/// escalates to a <b>library-initiated close of the whole connection</b> (code=541), because
/// <c>SessionManager.Lookup</c> does an indexer read on a dictionary it may have just mutated.
/// Wolverine cannot catch that — it is raised on the client's own <c>MainLoop</c> thread — so the
/// only thing Wolverine owns is what happens next. Locally the connection died and the affected
/// tests still passed, which is exactly the kind of "observed, never asserted" behavior that quietly
/// stops being true.
/// </para>
///
/// <para>
/// This kills the connection from the BROKER side through the management API rather than trying to
/// provoke the 541 race, for two reasons: the race is timing dependent and would make a flaky test,
/// and a forced close is the same thing from Wolverine's point of view — an unsolicited shutdown it
/// did not initiate. What is being pinned is the recovery contract, not the client bug.
/// </para>
///
/// <para>
/// Asserting on <c>ReconnectAttempts</c> alone would be too weak: the connection coming back does
/// not mean the CONSUMER came back. <see cref="ConnectionMonitor"/> has to rebuild every tracked
/// channel agent on recovery (see #3370, where an agent falling out of that list was one drop away
/// from being permanently ghosted). So the real assertion is that a message published after the kill
/// is still received.
/// </para>
/// </summary>
public class connection_recovery_after_a_broker_side_kill
{
    [Fact]
    public async Task listeners_resume_after_the_connection_is_killed_underneath_them()
    {
        var queueName = RabbitTesting.NextQueueName();
        var clientName = "wolverine-3950-" + Guid.NewGuid().ToString("N")[..8];

        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                // A distinct client-provided name is what lets the management API find exactly this
                // host's connections and leave every other test's alone.
                opts.UseRabbitMq(f => f.ClientProvidedName = clientName)
                    .AutoProvision().AutoPurgeOnStartup();

                opts.PublishAllMessages().ToRabbitQueue(queueName);
                opts.ListenToRabbitQueue(queueName);
            })
            .StartAsync(cancellationToken: TestContext.Current.CancellationToken);

        // Baseline: prove the listener works before anything is broken, so a failure below cannot be
        // "it never worked".
        await host.TrackActivity()
            .IncludeExternalTransports()
            .Timeout(30.Seconds())
            .SendMessageAndWaitAsync(new ConnectionKillMessage(Guid.NewGuid()));

        var transport = host.GetRuntime().Options.Transports.GetOrCreate<RabbitMqTransport>();
        var reconnectsBefore = transport.ReconnectAttempts;

        var killed = await killConnectionsAsync(clientName);
        killed.ShouldBeGreaterThan(0, "the management API found no connection for this host to kill");

        // RabbitMQ.Client's auto-recovery runs on its own interval (5s by default), so this is a
        // poll rather than a wait on any Wolverine signal.
        await waitForAsync(() => transport.ReconnectAttempts > reconnectsBefore, 60.Seconds(),
            "the RabbitMQ connection never reported a recovery");

        // The point of the test. A recovered CONNECTION with a ghosted consumer would satisfy the
        // check above and still never deliver this.
        await host.TrackActivity()
            .IncludeExternalTransports()
            .Timeout(60.Seconds())
            .SendMessageAndWaitAsync(new ConnectionKillMessage(Guid.NewGuid()));
    }

    private static async Task waitForAsync(Func<bool> condition, TimeSpan timeout, string message)
    {
        var deadline = DateTimeOffset.UtcNow.Add(timeout);
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (condition()) return;
            await Task.Delay(250.Milliseconds());
        }

        throw new TimeoutException(message);
    }

    // Force-closes every connection whose client-provided name matches, and answers how many were
    // closed so the test can fail loudly rather than silently "recovering" from nothing.
    private static async Task<int> killConnectionsAsync(string clientName)
    {
        var credentials = new NetworkCredential("guest", "guest");
        using var handler = new HttpClientHandler { Credentials = credentials };
        using var client = new HttpClient(handler);

        // /api/connections is fed by RabbitMQ's connection tracking, which lags the actual TCP
        // connect by several seconds on 4.x -- measured locally as empty at 4s and populated at 8s
        // with the host already running and passing traffic. Polling rather than reading once is the
        // difference between this test working and reporting "no connection to kill".
        var names = await pollForConnectionNamesAsync(client, clientName);

        foreach (var name in names)
        {
            var request = new HttpRequestMessage(HttpMethod.Delete,
                $"http://localhost:15672/api/connections/{Uri.EscapeDataString(name)}");

            await client.SendAsync(request);
        }

        return names.Length;
    }

    private static async Task<string[]> pollForConnectionNamesAsync(HttpClient client, string clientName)
    {
        var deadline = DateTimeOffset.UtcNow.Add(60.Seconds());
        var observed = "(never queried)";

        while (DateTimeOffset.UtcNow < deadline)
        {
            var json = await client.GetStringAsync("http://localhost:15672/api/connections");
            var all = JsonDocument.Parse(json).RootElement.EnumerateArray().ToArray();

            var names = all
                .Where(x => x.TryGetProperty("client_properties", out var props)
                            && props.TryGetProperty("connection_name", out var name)
                            && name.GetString() == clientName)
                .Select(x => x.GetProperty("name").GetString()!)
                .ToArray();

            if (names.Length > 0) return names;

            observed = all
                .Select(x => x.TryGetProperty("client_properties", out var props)
                             && props.TryGetProperty("connection_name", out var n)
                    ? n.GetString() ?? "(null)"
                    : "(none)")
                .Join(", ");

            await Task.Delay(1.Seconds());
        }

        throw new TimeoutException(
            $"No RabbitMQ connection reported connection_name '{clientName}' within the timeout. Last observed: [{observed}]");
    }
}

public record ConnectionKillMessage(Guid Id);

public static class ConnectionKillMessageHandler
{
    public static void Handle(ConnectionKillMessage message)
    {
    }
}
