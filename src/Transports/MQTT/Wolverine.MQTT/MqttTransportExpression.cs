using JasperFx.Core.Reflection;
using Wolverine.Configuration;
using Wolverine.MQTT.Internals;

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

            if (!e.Subscriptions.Any())
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
}