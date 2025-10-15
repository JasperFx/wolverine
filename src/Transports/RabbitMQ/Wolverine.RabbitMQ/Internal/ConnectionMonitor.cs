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
        
        _connection.ConnectionShutdownAsync += connectionOnConnectionShutdownAsync;
        _connection.ConnectionUnblockedAsync += connectionOnConnectionUnblockedAsync;
        _connection.ConnectionBlockedAsync += connectionOnConnectionBlockedAsync;
        _connection.CallbackExceptionAsync += connectionOnCallbackExceptionAsync;
        _connection.RecoverySucceededAsync += connectionOnRecoverySucceededAsync;
    }

    public Task<IChannel> CreateChannelAsync()
    {
        if (_connection == null) throw new InvalidOperationException("The connection is not initialized");

        var options = new CreateChannelOptions(false, false, null, null);
        options = _transport.ApplyChannelOptions(options);

        return _connection.CreateChannelAsync(options);
    }

    public ConnectionRole Role { get; }

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
        _logger.LogInformation("Rabbit MQ connection is blocked because of {Reason}", e.Reason);
        return Task.CompletedTask;
    }

    private Task connectionOnConnectionUnblockedAsync(object? sender, AsyncEventArgs e)
    {
        _logger.LogInformation("Rabbit MQ connection unblocked");
        return Task.CompletedTask;
    }

    private Task connectionOnConnectionShutdownAsync(object? sender, ShutdownEventArgs e)
    {
        if (e.Initiator == ShutdownInitiator.Application) return Task.CompletedTask;

        if (e.Exception != null)
        {
            _logger.LogError(e.Exception, "Unexpected Rabbit MQ connection shutdown");
        }
        return Task.CompletedTask;
    }

    public void Remove(RabbitMqChannelAgent agent)
    {
        _agents.Remove(agent);
    }
}