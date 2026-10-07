using System.Net;
using System.Text.Json;
using JasperFx.Core;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RabbitMQ.Client;
using Shouldly;
using Wolverine.RabbitMQ.Internal;
using Wolverine.Runtime;
using Wolverine.Tracking;
using Wolverine.Transports;
using Xunit;

namespace Wolverine.RabbitMQ.Tests.Bugs;

/// <summary>
/// Regression coverage for #4864.
///
/// <para>
/// <c>ConnectionMonitor.connectionOnRecoverySucceededAsync</c> rebuilt every tracked channel agent in
/// a plain <c>foreach</c> with no per-agent error handling. The first <c>ReconnectedAsync()</c> that
/// threw (a <c>BasicConsumeAsync</c> that timed out while a quorum queue had no majority, or a
/// <c>teardownChannel()</c> on a channel the client had already disposed) ended the loop, and every
/// agent after it in the list was never rebuilt. Those listeners stayed <c>Disconnected</c> on a
/// recovered connection -- rejecting every delivery or holding no consumer at all -- while the
/// connection reported healthy and <c>ListeningAgent.Status</c> stayed <c>Accepting</c>.
/// </para>
///
/// <para>
/// The failure is injected with a tracked fake agent that throws on its first rebuild, placed ahead
/// of the real listener in the monitor's list. The connection is then killed from the broker side,
/// the same way as <c>connection_recovery_after_a_broker_side_kill</c>, so RabbitMQ.Client's own
/// recovery raises <c>RecoverySucceededAsync</c> for real.
/// </para>
/// </summary>
public class Bug_4864_recovery_loop_survives_a_failing_agent : IAsyncLifetime
{
    private readonly string _queueName = $"bug4864-{Guid.NewGuid():N}";
    private readonly string _clientName = "wolverine-4864-" + Guid.NewGuid().ToString("N")[..8];
    private IHost _host = null!;
    private FailingAgent? _failing;

    public async ValueTask InitializeAsync()
    {
        _host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.ServiceName = "Bug4864";

                // The client-provided name is what lets the management API find exactly this host's
                // connections and leave every other test's alone.
                opts.UseRabbitMq(f => f.ClientProvidedName = _clientName)
                    .AutoProvision()
                    .AutoPurgeOnStartup();

                opts.PublishMessage<Bug4864Message>().ToRabbitQueue(_queueName);
                opts.ListenToRabbitQueue(_queueName);

                opts.LocalRoutingConventionDisabled = true;
            }).StartAsync();
    }

    public async ValueTask DisposeAsync()
    {
        if (_failing is not null)
        {
            await _failing.DisposeAsync();
        }

        await _host.StopAsync();
        _host.Dispose();
    }

    [Fact]
    public async Task agents_behind_a_failing_agent_are_still_rebuilt_and_the_failing_one_is_retried()
    {
        // Prove the pipe works before anything is broken, so a failure below cannot be "it never worked".
        await publishAndExpectHandledAsync("before-kill");

        var transport = _host.GetRuntime().Options.RabbitMqTransport();
        var monitor = transport.ListeningConnection;

        // The recovery loop walks the monitor's agents in tracking order, so the fake has to sit in
        // front of the real listener. Agents are tracked when they are constructed: track the fake,
        // then restart the listener so a freshly built RabbitMqListener lands behind it.
        _failing = new FailingAgent(monitor, transport.Logger, failuresBeforeSuccess: 1);
        var listeningAgent = _host.GetRuntime().Endpoints.FindListeningAgent(queueUri())!;
        await listeningAgent.RestartAsync(force: true);

        var tracked = monitor.TrackedAgents.ToList();
        var listener = findListener();
        tracked.IndexOf(_failing).ShouldBeLessThan(tracked.IndexOf(listener),
            "the fake agent must be rebuilt before the real listener for the test to mean anything");

        await publishAndExpectHandledAsync("after-listener-restart");

        var reconnectsBefore = transport.ReconnectAttempts;
        var killed = await killConnectionsAsync(_clientName);
        killed.ShouldBeGreaterThan(0, "the management API found no connection for this host to kill");

        // RabbitMQ.Client's auto-recovery runs on its own interval (5s by default), so this is a
        // poll rather than a wait on any Wolverine signal.
        (await waitForAsync(() => transport.ReconnectAttempts > reconnectsBefore, 60.Seconds()))
            .ShouldBeTrue("the RabbitMQ connection never reported a recovery");

        // ReconnectAttempts is transport-wide, so the sending connection's recovery can bump it before
        // the listening connection's loop has reached the fake. Wait for the loop rather than assume it.
        (await waitForAsync(() => _failing.Attempts >= 1, 30.Seconds()))
            .ShouldBeTrue("the recovery loop should have reached the fake agent");

        // The bug. The loop died on the fake, so the listener behind it was never rebuilt and this
        // message was rejected back to the queue forever.
        await publishAndExpectHandledAsync("after-recovery");

        // A failed rebuild is retried rather than abandoned: the fake succeeds on its second attempt.
        (await waitForAsync(() => _failing.State == AgentState.Connected, 30.Seconds()))
            .ShouldBeTrue($"the failing agent should have been retried; attempts = {_failing.Attempts}");
        _failing.Attempts.ShouldBe(2);
    }

    [Fact]
    public async Task teardown_of_a_channel_the_client_already_disposed_does_not_throw()
    {
        // Eleven of the fifteen production occurrences behind #4864 were teardownChannel() throwing
        // ObjectDisposedException out of AutorecoveringChannel.CloseAsync on a channel the client had
        // already disposed during its own recovery. One throw was enough to abort the whole loop.
        var transport = _host.GetRuntime().Options.RabbitMqTransport();
        var monitor = transport.ListeningConnection;

        _failing = new FailingAgent(monitor, transport.Logger, failuresBeforeSuccess: 0);

        var channel = await monitor.CreateChannelAsync();
        await channel.CloseAsync(TestContext.Current.CancellationToken);
        channel.Dispose();

        _failing.Channel = channel;

        await Should.NotThrowAsync(() => _failing.TeardownAsync());

        _failing.Channel.ShouldBeNull();
        _failing.State.ShouldBe(AgentState.Disconnected);
    }

    private Uri queueUri()
    {
        return new Uri($"rabbitmq://queue/{_queueName}");
    }

    private RabbitMqListener findListener()
    {
        var agent = _host.GetRuntime().Endpoints.ActiveListeners().Single(x => x.Uri == queueUri());
        return ((ListeningAgent)agent).Listener.ShouldBeOfType<RabbitMqListener>();
    }

    private async Task publishAndExpectHandledAsync(string value)
    {
        await _host.MessageBus().PublishAsync(new Bug4864Message(value));

        var handled = await waitForAsync(() =>
        {
            lock (Bug4864MessageHandler.Received)
            {
                return Bug4864MessageHandler.Received.Contains(value);
            }
        }, 60.Seconds());

        handled.ShouldBeTrue($"Expected the message '{value}' to be received and handled");
    }

    private static async Task<bool> waitForAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (condition()) return true;
            await Task.Delay(250);
        }

        return condition();
    }

    // Force-closes every connection whose client-provided name matches, and answers how many were
    // closed so the test can fail loudly rather than silently "recovering" from nothing.
    private static async Task<int> killConnectionsAsync(string clientName)
    {
        var credentials = new NetworkCredential("guest", "guest");
        using var handler = new HttpClientHandler { Credentials = credentials };
        using var client = new HttpClient(handler);

        // /api/connections lags the actual TCP connect by several seconds on RabbitMQ 4.x, so poll
        // rather than read once.
        var names = Array.Empty<string>();
        var deadline = DateTimeOffset.UtcNow + 60.Seconds();
        while (names.Length == 0 && DateTimeOffset.UtcNow < deadline)
        {
            var json = await client.GetStringAsync("http://localhost:15672/api/connections");
            names = JsonDocument.Parse(json).RootElement.EnumerateArray()
                .Where(x => x.TryGetProperty("client_properties", out var props)
                            && props.TryGetProperty("connection_name", out var name)
                            && name.GetString() == clientName)
                .Select(x => x.GetProperty("name").GetString()!)
                .ToArray();

            if (names.Length == 0) await Task.Delay(1.Seconds());
        }

        foreach (var name in names)
        {
            using var request = new HttpRequestMessage(HttpMethod.Delete,
                $"http://localhost:15672/api/connections/{Uri.EscapeDataString(name)}");
            await client.SendAsync(request);
        }

        return names.Length;
    }

    /// <summary>
    /// A tracked channel agent whose rebuild fails a configured number of times before succeeding,
    /// standing in for a listener whose BasicConsume times out or whose channel teardown throws.
    /// </summary>
    internal class FailingAgent : RabbitMqChannelAgent
    {
        private readonly int _failuresBeforeSuccess;

        public FailingAgent(ConnectionMonitor monitor, ILogger logger, int failuresBeforeSuccess)
            : base(monitor, logger)
        {
            _failuresBeforeSuccess = failuresBeforeSuccess;
        }

        public int Attempts { get; private set; }

        internal override Task ReconnectedAsync()
        {
            Attempts++;
            if (Attempts <= _failuresBeforeSuccess)
            {
                throw new InvalidOperationException($"Bug4864: simulated rebuild failure on attempt {Attempts}");
            }

            return base.ReconnectedAsync();
        }

        public Task TeardownAsync()
        {
            return teardownChannel();
        }

        public override string ToString()
        {
            return "Bug4864 failing agent";
        }
    }
}

public record Bug4864Message(string Value);

public static class Bug4864MessageHandler
{
    public static readonly List<string> Received = new();

    public static void Handle(Bug4864Message message)
    {
        lock (Received)
        {
            Received.Add(message.Value);
        }
    }
}
