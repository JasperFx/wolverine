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
    IModel CreateModel();
    ConnectionRole Role { get; }
}

internal class ConnectionMonitor : IDisposable, IConnectionMonitor
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

        _connection = transport.AmqpTcpEndpoints.Any()
            ? transport.ConnectionFactory.CreateConnection(transport.AmqpTcpEndpoints)
            : transport.ConnectionFactory.CreateConnection();

        _connection.ConnectionShutdown += connectionOnConnectionShutdown;
        _connection.ConnectionUnblocked += connectionOnConnectionUnblocked;
        _connection.ConnectionBlocked += connectionOnConnectionBlocked;
        _connection.CallbackException += connectionOnCallbackException;
    }

    public IModel CreateModel()
    {
        if (_connection == null) throw new InvalidOperationException("The connection is not initialized");

        return _connection!.CreateModel();
    }

    public ConnectionRole Role { get; }

    public void Dispose()
    {
        try
        {
            _connection?.Close();
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

    private void connectionOnCallbackException(object? sender, CallbackExceptionEventArgs e)
    {
        if (e.Exception != null)
        {
            _logger.LogError(e.Exception, "Rabbit MQ connection error on callback");
        }
    }

    private void connectionOnConnectionBlocked(object? sender, ConnectionBlockedEventArgs e)
    {
        _logger.LogInformation("Rabbit MQ connection is blocked because of {Reason}", e.Reason);
    }

    private void connectionOnConnectionUnblocked(object? sender, EventArgs e)
    {
        _logger.LogInformation("Rabbit MQ connection unblocked");
    }

    private void connectionOnConnectionShutdown(object? sender, ShutdownEventArgs e)
    {
        if (e.Initiator == ShutdownInitiator.Application) return;

        if (e.Exception != null)
        {
            _logger.LogError(e.Exception, "Unexpected Rabbit MQ connection shutdown");
        }
    }

    public void Remove(RabbitMqChannelAgent agent)
    {
        _agents.Remove(agent);
    }
}