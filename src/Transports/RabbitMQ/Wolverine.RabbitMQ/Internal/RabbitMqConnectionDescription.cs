using JasperFx.Descriptors;
using RabbitMQ.Client;

namespace Wolverine.RabbitMQ.Internal;

/// <summary>
/// Secret-safe description of a <see cref="ConnectionFactory"/> for use with
/// <see cref="OptionsDescription"/>. Exposes non-secret connection fields
/// (host, port, vhost, username, SSL, heartbeat) and deliberately omits the
/// password and any credential-carrying properties.
/// </summary>
public sealed class RabbitMqConnectionDescription : IDescribeMyself
{
    private readonly ConnectionFactory _factory;

    public RabbitMqConnectionDescription(ConnectionFactory factory)
    {
        _factory = factory;
    }

    public OptionsDescription ToDescription()
    {
        var description = new OptionsDescription
        {
            Subject = "Wolverine.RabbitMQ.ConnectionFactory",
            Title = "Connection"
        };

        description.AddValue(nameof(_factory.HostName), _factory.HostName);
        description.AddValue(nameof(_factory.Port), _factory.Port);
        description.AddValue(nameof(_factory.VirtualHost), _factory.VirtualHost);
        description.AddValue(nameof(_factory.UserName), _factory.UserName);
        // Password intentionally omitted.

        description.AddValue(nameof(_factory.RequestedHeartbeat), _factory.RequestedHeartbeat);
        description.AddValue(nameof(_factory.RequestedConnectionTimeout), _factory.RequestedConnectionTimeout);
        description.AddValue(nameof(_factory.ClientProvidedName), _factory.ClientProvidedName ?? string.Empty);
        description.AddValue(nameof(_factory.AutomaticRecoveryEnabled), _factory.AutomaticRecoveryEnabled);
        description.AddValue(nameof(_factory.TopologyRecoveryEnabled), _factory.TopologyRecoveryEnabled);

        if (_factory.Ssl != null)
        {
            description.AddValue("Ssl.Enabled", _factory.Ssl.Enabled);
            if (_factory.Ssl.Enabled)
            {
                description.AddValue("Ssl.ServerName", _factory.Ssl.ServerName ?? string.Empty);
                description.AddValue("Ssl.Version", _factory.Ssl.Version.ToString());
            }
        }

        return description;
    }
}
