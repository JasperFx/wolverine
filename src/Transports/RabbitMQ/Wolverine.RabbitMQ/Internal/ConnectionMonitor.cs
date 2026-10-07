using JasperFx.Core;
using Microsoft.Extensions.Logging;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace Wolverine.RabbitMQ.Internal;

public enum ConnectionRole
{
    Listening,
    Sending
}

public interface IConnectionMonitor
{
    Task ConnectAsync();
    /// <param name="consumerDispatchConcurrency">
    /// Overrides the transport-wide <see cref="WolverineRabbitMqChannelOptions.ConsumerDispatchConcurrency"/>
    /// for this one channel. Listeners use it to scale a single endpoint's consumption without
    /// changing every other channel in the process (GH-3492).
    /// </param>
    Task<IChannel> CreateChannelAsync(ushort? consumerDispatchConcurrency = null);
    ConnectionRole Role { get; }
}

internal class ConnectionMonitor : IAsyncDisposable, IConnectionMonitor
{
    private readonly RabbitMqTransport _transport;
    private readonly ILogger<RabbitMqTransport> _logger;
    private readonly object _agentsLock = new();
    private readonly List<RabbitMqChannelAgent> _agents = [];
    private IConnection? _connection;

    // Bumped on every successful recovery. A rebuild retry still running from an earlier recovery
    // compares against it so it can step aside once a later recovery has rebuilt everything anyway.
    private int _recoveryGeneration;

    /// <summary>
    /// Delays between successive attempts to rebuild an agent whose rebuild failed during connection
    /// recovery (GH-4864). The first retry comes quickly because the usual cause -- a quorum queue
    /// without a majority, or a channel the client was still disposing -- clears within seconds; the
    /// tail is there for a broker that takes a while to settle after a roll.
    /// </summary>
    internal static readonly TimeSpan[] RebuildRetryDelays =
    [
        TimeSpan.FromSeconds(1),
        TimeSpan.FromSeconds(2),
        TimeSpan.FromSeconds(5),
        TimeSpan.FromSeconds(10),
        TimeSpan.FromSeconds(20),
        TimeSpan.FromSeconds(30)
    ];

    public ConnectionMonitor(RabbitMqTransport transport, ConnectionRole role)
    {
        _transport = transport;
        Role = role;
        _logger = transport.Logger;
    }
    
    public async Task ConnectAsync()
    {
        var connection = await _transport.CreateConnectionAsync();
        _connection = connection;
        IsConnected = true;
        // Initial connection -- record the timestamp but don't bump the
        // reconnect counter (that's reserved for genuine recoveries).
        _transport.RecordInitialConnection();

        connection.ConnectionShutdownAsync += connectionOnConnectionShutdownAsync;
        connection.ConnectionUnblockedAsync += connectionOnConnectionUnblockedAsync;
        connection.ConnectionBlockedAsync += connectionOnConnectionBlockedAsync;
        connection.CallbackExceptionAsync += connectionOnCallbackExceptionAsync;
        connection.RecoverySucceededAsync += connectionOnRecoverySucceededAsync;
    }

    /// <summary>
    /// Asynchronously creates a new channel for communication with RabbitMQ.
    /// Configures the channel using custom RabbitMQ channel creation options if specified.
    /// </summary>
    /// <returns>A task that resolves to an <see cref="IChannel"/> instance for RabbitMQ communication.</returns>
    public Task<IChannel> CreateChannelAsync(ushort? consumerDispatchConcurrency = null)
    {
        var connection = _connection
            ?? throw new InvalidOperationException(RabbitMqTransport.NotInitializedMessage(Role));

        var wolverineOptions = new WolverineRabbitMqChannelOptions();
        _transport.ChannelCreationOptions?.Invoke(wolverineOptions);

        var options = new CreateChannelOptions(wolverineOptions.PublisherConfirmationsEnabled,
            wolverineOptions.PublisherConfirmationTrackingEnabled,
            consumerDispatchConcurrency: consumerDispatchConcurrency ?? wolverineOptions.ConsumerDispatchConcurrency);

        return connection.CreateChannelAsync(options);
    }

    public ConnectionRole Role { get; }

    /// <summary>
    /// Whether the underlying RabbitMQ connection is currently open.
    /// Thread-safe: read from health check threads, written from connection event callbacks.
    /// </summary>
    public volatile bool IsConnected;

    /// <summary>
    /// Whether the RabbitMQ connection is currently blocked by the broker (resource alarm).
    /// </summary>
    public volatile bool IsBlocked;

    public async ValueTask DisposeAsync()
    {
        var connection = _connection;
        if (connection is null)
            return;
        _connection = null;

        try
        {
            connection.ConnectionShutdownAsync -= connectionOnConnectionShutdownAsync;
            connection.ConnectionUnblockedAsync -= connectionOnConnectionUnblockedAsync;
            connection.ConnectionBlockedAsync -= connectionOnConnectionBlockedAsync;
            connection.CallbackExceptionAsync -= connectionOnCallbackExceptionAsync;
            connection.RecoverySucceededAsync -= connectionOnRecoverySucceededAsync;

            await connection.CloseAsync();
        }
        catch (ObjectDisposedException)
        {
        }

        connection.SafeDispose();
    }

    public void Track(RabbitMqChannelAgent agent)
    {
        lock (_agentsLock)
            _agents.Add(agent);
    }

    /// <summary>
    /// The channel agents currently tracked by this monitor. Only agents in this list are
    /// rebuilt by <see cref="connectionOnRecoverySucceededAsync"/>, so an agent that falls out
    /// of it is one connection drop away from being permanently ghosted (see #3370). Exposed
    /// for regression coverage of that invariant.
    /// </summary>
    internal IReadOnlyList<RabbitMqChannelAgent> TrackedAgents
    {
        get
        {
            lock (_agentsLock)
                return [.. _agents];
        }
    }

    private async Task connectionOnRecoverySucceededAsync(object sender, AsyncEventArgs @event)
    {
        IsConnected = true;
        _transport.RecordReconnection();

        var generation = Interlocked.Increment(ref _recoveryGeneration);

        RabbitMqChannelAgent[] agentsSnapshot;
        lock(_agentsLock)
            agentsSnapshot = [.. _agents];

        // GH-4864: one agent's rebuild must not stop the rest. This handler used to await every
        // ReconnectedAsync() in a bare foreach, so the first one that threw -- a BasicConsume that
        // timed out while a quorum queue had no majority, or a teardown of a channel the client had
        // already disposed -- ended the loop. Every agent behind it kept a channel with no consumer
        // while the connection reported healthy, and the only trace was the client's
        // CallbackExceptionAsync. Rebuild each agent on its own, and retry the ones that failed.
        var failed = new List<RabbitMqChannelAgent>();
        foreach (var agent in agentsSnapshot)
        {
            try
            {
                await agent.ReconnectedAsync();
            }
            catch (Exception e)
            {
                failed.Add(agent);
                _logger.LogError(e,
                    "Failed to rebuild Rabbit MQ {Role} agent {Agent} after connection recovery; it will be retried",
                    Role, agent);
            }
        }

        if (failed.Count == 0)
        {
            _logger.LogInformation("RabbitMQ connection is recovered successfully");
            return;
        }

        _logger.LogWarning(
            "RabbitMQ connection is recovered, but {Failed} of {Total} {Role} agent(s) could not be rebuilt yet and will be retried",
            failed.Count, agentsSnapshot.Length, Role);

        // Off the client's recovery callback: the retries sleep between attempts, and the callback
        // must not hold up the client's own recovery of the other connection or of later events.
        _ = Task.Run(() => retryFailedRebuildsAsync(failed, generation));
    }

    /// <summary>
    /// Retries the agents whose rebuild failed in <see cref="connectionOnRecoverySucceededAsync"/>, on
    /// the schedule in <see cref="RebuildRetryDelays"/>. Stops early when the connection has gone again
    /// or a later recovery has superseded this one, because that later recovery rebuilds every tracked
    /// agent anyway; and skips agents that have been disposed in the meantime.
    /// </summary>
    private async Task retryFailedRebuildsAsync(List<RabbitMqChannelAgent> agents, int generation)
    {
        var remaining = agents;

        foreach (var delay in RebuildRetryDelays)
        {
            await Task.Delay(delay);

            if (_connection is null || !IsConnected || Volatile.Read(ref _recoveryGeneration) != generation)
            {
                return;
            }

            var stillFailing = new List<RabbitMqChannelAgent>();
            foreach (var agent in remaining)
            {
                if (agent.IsDisposed || !isTracked(agent))
                {
                    continue;
                }

                try
                {
                    await agent.ReconnectedAsync();
                    _logger.LogInformation("Rebuilt Rabbit MQ {Role} agent {Agent} after a failed connection recovery",
                        Role, agent);
                }
                catch (Exception e)
                {
                    stillFailing.Add(agent);
                    _logger.LogWarning(e,
                        "Rebuilding Rabbit MQ {Role} agent {Agent} failed again; retrying in {Delay}",
                        Role, agent, delay);
                }
            }

            if (stillFailing.Count == 0)
            {
                return;
            }

            remaining = stillFailing;
        }

        foreach (var agent in remaining)
        {
            _logger.LogError(
                "Gave up rebuilding Rabbit MQ {Role} agent {Agent} after {Attempts} attempts; it will not receive or send until the next connection recovery",
                Role, agent, RebuildRetryDelays.Length + 1);
        }
    }

    private bool isTracked(RabbitMqChannelAgent agent)
    {
        lock (_agentsLock)
            return _agents.Contains(agent);
    }

    private Task connectionOnCallbackExceptionAsync(object? sender, CallbackExceptionEventArgs e)
    {
        if (e.Exception != null)
        {
            _logger.LogError(e.Exception, "Rabbit MQ connection error on callback");
        }

        return Task.CompletedTask;
    }

    private Task connectionOnConnectionBlockedAsync(object? sender, ConnectionBlockedEventArgs e)
    {
        IsBlocked = true;
        _logger.LogInformation("Rabbit MQ connection is blocked because of {Reason}", e.Reason);
        return Task.CompletedTask;
    }

    private Task connectionOnConnectionUnblockedAsync(object? sender, AsyncEventArgs e)
    {
        IsBlocked = false;
        _logger.LogInformation("Rabbit MQ connection unblocked");
        return Task.CompletedTask;
    }

    private Task connectionOnConnectionShutdownAsync(object? sender, ShutdownEventArgs e)
    {
        IsConnected = false;

        // Capture the close reason for health snapshots (host-initiated shutdowns
        // included — they're informative if the probe is called mid-shutdown).
        _transport.RecordShutdown(e);

        if (e.Initiator == ShutdownInitiator.Application) return Task.CompletedTask;

        if (e.Exception != null)
        {
            _logger.LogError(e.Exception, "Unexpected Rabbit MQ connection shutdown");
        }
        return Task.CompletedTask;
    }

    /// <summary>
    /// Exposes the underlying RabbitMQ connection for diagnostics and probes.
    /// May be null before <see cref="ConnectAsync"/> has run or after disposal.
    /// </summary>
    internal IConnection? Connection => _connection;

    public void Remove(RabbitMqChannelAgent agent)
    {
        lock (_agentsLock)
            _agents.Remove(agent);
    }
}