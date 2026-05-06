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
    Task<IChannel> CreateChannelAsync();
    ConnectionRole Role { get; }
}

internal class ConnectionMonitor : IAsyncDisposable, IConnectionMonitor
{
    private readonly RabbitMqTransport _transport;
    private readonly ILogger<RabbitMqTransport> _logger;
    private readonly List<RabbitMqChannelAgent> _agents = new();
    private IConnection? _connection;

    public ConnectionMonitor(RabbitMqTransport transport, ConnectionRole role)
    {
        _transport = transport;
        Role = role;
        _logger = transport.Logger;
    }
    
    public async Task ConnectAsync()
    {
        _connection = await _transport.CreateConnectionAsync();
        IsConnected = true;
        // Initial connection -- record the timestamp but don't bump the
        // reconnect counter (that's reserved for genuine recoveries).
        _transport.RecordInitialConnection();

        _connection.ConnectionShutdownAsync += connectionOnConnectionShutdownAsync;
        _connection.ConnectionUnblockedAsync += connectionOnConnectionUnblockedAsync;
        _connection.ConnectionBlockedAsync += connectionOnConnectionBlockedAsync;
        _connection.CallbackExceptionAsync += connectionOnCallbackExceptionAsync;
        _connection.RecoverySucceededAsync += connectionOnRecoverySucceededAsync;
    }

    /// <summary>
    /// Asynchronously creates a new channel for communication with RabbitMQ.
    /// Configures the channel using custom RabbitMQ channel creation options if specified.
    /// </summary>
    /// <returns>A task that resolves to an <see cref="IChannel"/> instance for RabbitMQ communication.</returns>
    public Task<IChannel> CreateChannelAsync()
    {
        if (_connection == null) throw new InvalidOperationException("The connection is not initialized");

        var wolverineOptions = new WolverineRabbitMqChannelOptions();
        _transport.ChannelCreationOptions?.Invoke(wolverineOptions);

        var options = new CreateChannelOptions(wolverineOptions.PublisherConfirmationsEnabled, wolverineOptions.PublisherConfirmationTrackingEnabled, consumerDispatchConcurrency: wolverineOptions.ConsumerDispatchConcurrency);

        return _connection!.CreateChannelAsync(options);
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
        try
        {
            if(_connection is not null)
            {
                await _connection.CloseAsync();
            }
        }
        catch (ObjectDisposedException)
        {
        }

        _connection?.SafeDispose();
    }

    public void Track(RabbitMqChannelAgent agent)
    {
        _agents.Add(agent);
    }

    private async Task connectionOnRecoverySucceededAsync(object sender, AsyncEventArgs @event)
    {
        IsConnected = true;
        _transport.RecordReconnection();

        foreach (var agent in _agents)
        {
            await agent.ReconnectedAsync();
        }

        _logger.LogInformation("RabbitMQ connection is recovered successfully");
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
        _agents.Remove(agent);
    }
}