using MQTTnet.Extensions.ManagedClient;

namespace Wolverine.MQTT.Internals;

/// <summary>
/// Represents a single Wolverine tenant that is served by its own dedicated MQTT connection while sharing the
/// topic topology declared on the parent transport. Unlike NATS, MQTT tenants are <em>always</em>
/// own-connection: there is no subject/topic-prefix equivalent, so isolation is purely a matter of which broker
/// the tenant's dedicated <see cref="IManagedMqttClient"/> is connected to. Outbound routing is by
/// <see cref="Envelope.TenantId"/> via the framework's
/// <see cref="Wolverine.Transports.Sending.TenantedSender"/>; inbound the tenant's own listener stamps the
/// tenant id. Mirrors the RabbitMQ / Azure Service Bus tenant model.
/// </summary>
internal class MqttTenant
{
    public MqttTenant(string tenantId)
    {
        TenantId = tenantId ?? throw new ArgumentNullException(nameof(tenantId));
    }

    public string TenantId { get; }

    /// <summary>
    /// The tenant's own connection options. Seeded from the parent transport at registration time and then
    /// overridden with the tenant-specific broker configuration.
    /// </summary>
    public ManagedMqttClientOptions Options { get; set; } = new()
        { ClientOptions = new MQTTnet.Client.MqttClientOptions() };

    /// <summary>
    /// Optional JWT authentication configuration for the tenant's own connection.
    /// </summary>
    public MqttJwtAuthenticationOptions? Jwt { get; set; }

    /// <summary>
    /// The tenant's dedicated managed MQTT client. Created and owned by the transport during
    /// <see cref="MqttTransport.InitializeAsync"/>, and stopped with the transport.
    /// </summary>
    internal IManagedMqttClient? Client { get; set; }
}
