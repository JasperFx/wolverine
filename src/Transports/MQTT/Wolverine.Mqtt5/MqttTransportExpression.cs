using JasperFx.Core.Reflection;
using MQTTnet.Extensions.ManagedClient;
using Wolverine.Configuration;
using Wolverine.MQTT.Internals;
using Wolverine.Runtime;
using Wolverine.Transports.Sending;

namespace Wolverine.MQTT;

public class MqttTransportExpression
{
    private readonly MqttTransport _transport;
    private readonly WolverineOptions _options;

    internal MqttTransportExpression(MqttTransport transport, WolverineOptions options)
    {
        _transport = transport;
        _options = options;
    }

    /// <summary>
    ///     Apply a policy to all listening endpoints
    /// </summary>
    /// <param name="configure"></param>
    /// <returns></returns>
    public MqttTransportExpression ConfigureListeners(Action<MqttListenerConfiguration> configure)
    {
        var policy = new LambdaEndpointPolicy<MqttTopic>((e, _) =>
        {
            if (e.Role == EndpointRole.System)
            {
                return;
            }

            if (!e.IsListener)
            {
                return;
            }

            var configuration = new MqttListenerConfiguration(e);
            configure(configuration);

            configuration.As<IDelayedEndpointConfiguration>().Apply();
        });

        _options.Policies.Add(policy);

        return this.As<MqttTransportExpression>();
    }

    /// <summary>
    ///     Apply a policy to all MQTT subscribers
    /// </summary>
    /// <param name="configure"></param>
    /// <returns></returns>
    public MqttTransportExpression ConfigureSenders(Action<MqttSubscriberConfiguration> configure)
    {
        var policy = new LambdaEndpointPolicy<MqttTopic>((e, _) =>
        {
            if (e.Role == EndpointRole.System)
            {
                return;
            }

            if (!e.Subscriptions.Any() && e is not ITopicEndpoint)
            {
                return;
            }

            var configuration = new MqttSubscriberConfiguration(e);
            configure(configuration);

            configuration.As<IDelayedEndpointConfiguration>().Apply();
        });

        _options.Policies.Add(policy);

        return this.As<MqttTransportExpression>();
    }

    /// <summary>
    /// Override the routing behavior for unknown or missing tenant ids when using broker-per-tenant MQTT
    /// multi-tenancy (GH-3307). See <see cref="TenantedIdBehavior"/>. Default is
    /// <see cref="TenantedIdBehavior.FallbackToDefault"/>.
    /// </summary>
    /// <param name="behavior"></param>
    /// <returns></returns>
    public MqttTransportExpression TenantIdBehavior(TenantedIdBehavior behavior)
    {
        _transport.TenantedIdBehavior = behavior;
        return this;
    }

    /// <summary>
    /// Register a tenant that is served by its own dedicated MQTT connection (its own broker) while sharing the
    /// topic topology declared on this transport. Outbound messages carrying a matching
    /// <see cref="Envelope.TenantId"/> are routed to this tenant's connection; inbound messages consumed from it
    /// are stamped with the tenant id. The tenant connection is always given a unique ClientId derived from the
    /// tenant id, because MQTT brokers forcibly disconnect a second connection sharing a ClientId.
    /// </summary>
    /// <param name="tenantId"></param>
    /// <param name="configure">Configuration for the tenant's own broker connection</param>
    /// <param name="jwt">Optional OAUTH2-JWT authentication for the tenant's own connection.</param>
    /// <returns></returns>
    public MqttTransportExpression AddTenant(string tenantId, Action<ManagedMqttClientOptionsBuilder> configure,
        MqttJwtAuthenticationOptions? jwt = null)
    {
        if (string.IsNullOrEmpty(tenantId))
        {
            throw new ArgumentOutOfRangeException(nameof(tenantId), "Empty or null tenantId");
        }

        ArgumentNullException.ThrowIfNull(configure);

        var builder = new ManagedMqttClientOptionsBuilder();
        configure(builder);

        var tenant = _transport.Tenants[tenantId];
        tenant.Options = builder.Build();
        tenant.Jwt = jwt;

        return this;
    }

    /// <summary>
    /// Register a tenant that is served by its own dedicated MQTT connection (its own broker), bypassing the
    /// fluent builder. See <see cref="AddTenant(string, Action{ManagedMqttClientOptionsBuilder}, MqttJwtAuthenticationOptions)"/>.
    /// </summary>
    /// <param name="tenantId"></param>
    /// <param name="mqttOptions">The connection options for the tenant's own broker</param>
    /// <param name="jwt">Optional OAUTH2-JWT authentication for the tenant's own connection.</param>
    /// <returns></returns>
    public MqttTransportExpression AddTenant(string tenantId, ManagedMqttClientOptions mqttOptions,
        MqttJwtAuthenticationOptions? jwt = null)
    {
        if (string.IsNullOrEmpty(tenantId))
        {
            throw new ArgumentOutOfRangeException(nameof(tenantId), "Empty or null tenantId");
        }

        ArgumentNullException.ThrowIfNull(mqttOptions);

        var tenant = _transport.Tenants[tenantId];
        tenant.Options = mqttOptions;
        tenant.Jwt = jwt;

        return this;
    }
}