using JasperFx.Core;
using JasperFx.Core.Reflection;
using MQTTnet;
using MQTTnet.Extensions.ManagedClient;
using Wolverine.Configuration;
using Wolverine.MQTT.Internals;
using Wolverine.Runtime.Routing;

namespace Wolverine.MQTT;

public static class MqttTransportExtensions
{
    /// <summary>
    ///     Quick access to the MQTT Transport within this application.
    ///     This is for advanced usage
    /// </summary>
    /// <param name="endpoints"></param>
    /// <returns></returns>
    internal static MqttTransport MqttTransport(this WolverineOptions endpoints, BrokerName? name = null)
    {
        var transports = endpoints.As<WolverineOptions>().Transports;

        return transports.GetOrCreate<MqttTransport>(name);
    }

    /// <summary>
    /// Add a connection to an MQTT broker within this application
    /// </summary>
    /// <param name="options"></param>
    /// <param name="configure"></param>
    /// <param name="jwtAuthenticationOptions">Sets AuthenticationMethod to OAUTH2-JWT -- or whatever
    /// <see cref="MqttJwtAuthenticationOptions.AuthenticationMethod"/> is set to -- and uses the callback to fetch a token.
    /// When the configured period elapses, a new token is fetched and a ExtendedAuthenticationExchangeData with ReasonCode ReAuth is sent with the new token.
    /// <returns></returns>
    public static MqttTransportExpression UseMqtt(this WolverineOptions options,
        Action<ManagedMqttClientOptionsBuilder> configure,
        MqttJwtAuthenticationOptions? jwtAuthenticationOptions = null)
    {
        var transport = options.MqttTransport();
        var builder = new ManagedMqttClientOptionsBuilder();
        configure(builder);

        transport.Options = builder.Build();
        transport.JwtAuthenticationOptions = jwtAuthenticationOptions;

        return new MqttTransportExpression(transport, options);
    }

    /// <summary>
    /// Alternative to configuring MQTT that bypasses the MQTT fluent interface
    /// </summary>
    /// <param name="options"></param>
    /// <param name="mqttOptions"></param>
    /// <param name="jwtAuthenticationOptions">Sets AuthenticationMethod to OAUTH2-JWT -- or whatever
    /// <see cref="MqttJwtAuthenticationOptions.AuthenticationMethod"/> is set to -- and uses the callback to fetch a token.
    /// When the configured period elapses, a new token is fetched and a ExtendedAuthenticationExchangeData with ReasonCode ReAuth is sent with the new token.
    /// <returns></returns>
    public static MqttTransportExpression UseMqtt(this WolverineOptions options, ManagedMqttClientOptions mqttOptions,
        MqttJwtAuthenticationOptions? jwtAuthenticationOptions = null)
    {
        var transport = options.MqttTransport();

        transport.Options = mqttOptions;
        transport.JwtAuthenticationOptions = jwtAuthenticationOptions;

        return new MqttTransportExpression(transport, options);
    }

    /// <summary>
    /// Short hand method to use an MQTT transport connected to an MQTT broker running
    /// locally on the default port. Useful for testing scenarios
    /// </summary>
    /// <param name="options"></param>
    /// <param name="port">Optional override of the local broker port number</param>
    /// <returns></returns>
    public static MqttTransportExpression UseMqttWithLocalBroker(this WolverineOptions options, int? port = null)
    {
        return options.UseMqtt(builder =>
        {
            builder.WithClientOptions(opts => { opts.WithTcpServer("127.0.0.1", port); });
        });
    }

    /// <summary>
    /// Direct Wolverine to connect to MQTT using the named connection string from IConfiguration.
    /// This option is useful for Aspire integration between Wolverine and Aspire
    ///
    /// <para>
    /// The value is a broker URI. <c>mqtt://host:port</c> (what the CommunityToolkit Aspire Mosquitto integration
    /// supplies) and <c>tcp://host:port</c> connect over TCP, <c>mqtts://</c> (or <c>ssl://</c> / <c>tls://</c>) over
    /// TCP with TLS, and <c>ws://</c> / <c>wss://</c> over a web socket. A bare <c>host</c> or <c>host:port</c> is
    /// treated as TCP. The port defaults to 1883 (8883 for TLS), and <c>user:password@</c> in the URI becomes the
    /// client credentials.
    /// </para>
    /// </summary>
    /// <param name="options"></param>
    /// <param name="connectionStringName">Key of the expected connection string from IConfiguration</param>
    /// <param name="configureClient">Optional extra configuration of the MQTT client options (client id, TLS, credentials
    /// etc.), applied after the connection from the named connection string</param>
    /// <param name="jwtAuthenticationOptions">Sets AuthenticationMethod to OAUTH2-JWT -- or whatever
    /// <see cref="MqttJwtAuthenticationOptions.AuthenticationMethod"/> is set to -- and uses the callback to fetch a token.</param>
    /// <returns></returns>
    public static MqttTransportExpression UseMqttUsingNamedConnection(this WolverineOptions options,
        string connectionStringName, Action<MqttClientOptionsBuilder>? configureClient = null,
        MqttJwtAuthenticationOptions? jwtAuthenticationOptions = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionStringName);

        var transport = options.MqttTransport();
        applyNamedConnection(options, transport, connectionStringName, configureClient, jwtAuthenticationOptions);

        return new MqttTransportExpression(transport, options);
    }

    /// <summary>
    /// Configure a connection to a secondary, named MQTT broker using the named connection string from
    /// IConfiguration. This is the Aspire-friendly form of <c>AddNamedMqttBroker</c> for applications that talk
    /// to two or more MQTT brokers. The value formats are the same as <see cref="UseMqttUsingNamedConnection"/>.
    /// </summary>
    /// <param name="options"></param>
    /// <param name="name">Identity of the additional MQTT broker</param>
    /// <param name="connectionStringName">Key of the expected connection string from IConfiguration</param>
    /// <param name="configureClient">Optional extra configuration of the MQTT client options, applied after the
    /// connection from the named connection string</param>
    /// <param name="jwtAuthenticationOptions">Optional bearer token authentication for the additional broker</param>
    public static MqttTransportExpression AddNamedMqttBrokerUsingNamedConnection(this WolverineOptions options,
        BrokerName name, string connectionStringName, Action<MqttClientOptionsBuilder>? configureClient = null,
        MqttJwtAuthenticationOptions? jwtAuthenticationOptions = null)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionStringName);

        var transport = options.MqttTransport(name);
        applyNamedConnection(options, transport, connectionStringName, configureClient, jwtAuthenticationOptions);

        return new MqttTransportExpression(transport, options);
    }

    private static void applyNamedConnection(WolverineOptions options, MqttTransport transport,
        string connectionStringName, Action<MqttClientOptionsBuilder>? configureClient,
        MqttJwtAuthenticationOptions? jwtAuthenticationOptions)
    {
        transport.JwtAuthenticationOptions = jwtAuthenticationOptions;

        // GH-5126: MqttTransport.Options is only consumed when the transport connects (ConnectAsync), which is
        // after the named connection string is resolved and applied at the top of host startup. The extra
        // client configuration continues on the same MqttClientOptionsBuilder, so it can add to -- or override --
        // what the connection string set. (The MQTTnet 5 ManagedMqttClientOptionsBuilder shim replaces the whole
        // client options on every WithClientOptions call, so everything goes through this single call.)
        options.ApplyNamedConnectionString("MQTT", connectionStringName, "AddMosquitto", value =>
        {
            transport.Options = new ManagedMqttClientOptionsBuilder()
                .WithClientOptions(client =>
                {
                    ApplyConnectionString(client, value);
                    configureClient?.Invoke(client);
                })
                .Build();
        });
    }

    /// <summary>
    /// Apply an MQTT connection string (a broker URI or a bare host[:port]) to the client options builder
    /// </summary>
    internal static void ApplyConnectionString(MqttClientOptionsBuilder builder, string connectionString)
    {
        var value = connectionString.Trim();
        if (!value.Contains("://"))
        {
            value = "mqtt://" + value;
        }

        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Host.IsEmpty())
        {
            throw new ArgumentOutOfRangeException(nameof(connectionString),
                $"'{connectionString}' is not a valid MQTT connection string. Expected something like 'mqtt://localhost:1883'");
        }

        var scheme = uri.Scheme.ToLowerInvariant();
        switch (scheme)
        {
            case "mqtt":
            case "tcp":
                builder.WithTcpServer(uri.Host, uri.IsDefaultPort || uri.Port <= 0 ? 1883 : uri.Port);
                break;

            case "mqtts":
            case "ssl":
            case "tls":
                builder.WithTcpServer(uri.Host, uri.IsDefaultPort || uri.Port <= 0 ? 8883 : uri.Port);
                builder.WithTlsOptions(tls => tls.UseTls());
                break;

            case "ws":
            case "wss":
                // Credentials ride on the MQTT CONNECT packet, not on the web socket URI
                var socketUri = new UriBuilder(uri) { UserName = string.Empty, Password = string.Empty }.Uri.ToString();
                builder.WithWebSocketServer(ws => ws.WithUri(socketUri));
                if (scheme == "wss")
                {
                    builder.WithTlsOptions(tls => tls.UseTls());
                }

                break;

            default:
                throw new ArgumentOutOfRangeException(nameof(connectionString),
                    $"Unsupported MQTT connection string scheme '{uri.Scheme}'. Use mqtt://, mqtts://, tcp://, ws:// or wss://");
        }

        if (uri.UserInfo.IsNotEmpty())
        {
            var parts = uri.UserInfo.Split(':', 2);
            var userName = Uri.UnescapeDataString(parts[0]);
            var password = parts.Length > 1 ? Uri.UnescapeDataString(parts[1]) : string.Empty;
            builder.WithCredentials(userName, password);
        }
    }

    /// <summary>
    /// Configure a connection to a secondary, named MQTT broker used by this application. Only use this overload if
    /// your Wolverine application needs to talk to two or more MQTT brokers. The <paramref name="name"/> doubles as
    /// the URI scheme for the additional broker's endpoints, so pin publishing/listening to it with
    /// <see cref="ToMqttTopicOnNamedBroker"/> / <see cref="ListenToMqttTopicOnNamedBroker"/>.
    /// </summary>
    /// <param name="options"></param>
    /// <param name="name">Identity of the additional MQTT broker</param>
    /// <param name="configure">Configuration for the additional broker's connection</param>
    /// <param name="jwtAuthenticationOptions">Optional bearer token authentication for the additional broker. The
    /// authentication method defaults to OAUTH2-JWT and is overridable via <see cref="MqttJwtAuthenticationOptions.AuthenticationMethod"/>.</param>
    public static MqttTransportExpression AddNamedMqttBroker(this WolverineOptions options,
        BrokerName name,
        Action<ManagedMqttClientOptionsBuilder> configure,
        MqttJwtAuthenticationOptions? jwtAuthenticationOptions = null)
    {
        var transport = options.MqttTransport(name);
        var builder = new ManagedMqttClientOptionsBuilder();
        configure(builder);

        transport.Options = builder.Build();
        transport.JwtAuthenticationOptions = jwtAuthenticationOptions;

        return new MqttTransportExpression(transport, options);
    }

    /// <summary>
    /// Configure a connection to a secondary, named MQTT broker used by this application, bypassing the fluent
    /// builder. Only use this overload if your Wolverine application needs to talk to two or more MQTT brokers.
    /// </summary>
    /// <param name="options"></param>
    /// <param name="name">Identity of the additional MQTT broker</param>
    /// <param name="mqttOptions">The connection options for the additional broker</param>
    /// <param name="jwtAuthenticationOptions">Optional bearer token authentication for the additional broker. The
    /// authentication method defaults to OAUTH2-JWT and is overridable via <see cref="MqttJwtAuthenticationOptions.AuthenticationMethod"/>.</param>
    public static MqttTransportExpression AddNamedMqttBroker(this WolverineOptions options,
        BrokerName name,
        ManagedMqttClientOptions mqttOptions,
        MqttJwtAuthenticationOptions? jwtAuthenticationOptions = null)
    {
        var transport = options.MqttTransport(name);

        transport.Options = mqttOptions;
        transport.JwtAuthenticationOptions = jwtAuthenticationOptions;

        return new MqttTransportExpression(transport, options);
    }

    /// <summary>
    ///     Listen for incoming messages at the designated MQTT topic name
    /// </summary>
    /// <param name="endpoints"></param>
    /// <param name="topicName">The name of the Rabbit MQ queue</param>
    /// <param name="configure">
    ///     Optional configuration for this Rabbit Mq queue if being initialized by Wolverine
    ///     <returns></returns>
    public static MqttListenerConfiguration ListenToMqttTopic(this WolverineOptions endpoints, string topicName)
    {
        var transport = endpoints.MqttTransport();

        var endpoint = transport.Topics[topicName];
        endpoint.EndpointName = topicName;
        endpoint.IsListener = true;

        return new MqttListenerConfiguration(endpoint);
    }
    
    /// <summary>
    /// Listen for incoming messages at the designated MQTT topic name as part of a shared subscription group.
    /// This will configure the subscription topic in the format "$share/{sharedGroupName}/{topicName}".
    /// </summary>
    /// <param name="endpoints">The Wolverine application configuration.</param>
    /// <param name="topicName">The base topic name to listen to (e.g., "incoming/messages").</param>
    /// <param name="sharedGroupName">The name of the shared subscription group (e.g., "my-consumers").</param>
    /// <returns></returns>
    public static MqttListenerConfiguration ListenToMqttTopic(this WolverineOptions endpoints, string topicName, string sharedGroupName)
    {
        var transport = endpoints.MqttTransport();
        
        // Construct the full shared topic name
        var fullSharedTopicName = $"$share/{sharedGroupName}/{topicName}";

        var endpoint = transport.Topics[fullSharedTopicName];
        endpoint.EndpointName = fullSharedTopicName;
        endpoint.IsListener = true;

        return new MqttListenerConfiguration(endpoint);
    }

    /// <summary>
    /// Listen for incoming messages at the designated MQTT topic name on an additional, named broker registered via
    /// <see cref="AddNamedMqttBroker(WolverineOptions, BrokerName, Action{ManagedMqttClientOptionsBuilder}, MqttJwtAuthenticationOptions)"/>.
    /// </summary>
    /// <param name="endpoints"></param>
    /// <param name="name">Identity of the additional MQTT broker</param>
    /// <param name="topicName">The MQTT topic name to listen to</param>
    public static MqttListenerConfiguration ListenToMqttTopicOnNamedBroker(this WolverineOptions endpoints,
        BrokerName name, string topicName)
    {
        var transport = endpoints.MqttTransport(name);

        var endpoint = transport.Topics[topicName];
        endpoint.EndpointName = topicName;
        endpoint.IsListener = true;

        return new MqttListenerConfiguration(endpoint);
    }

    /// <summary>
    /// Publish messages to an MQTT topic
    /// </summary>
    /// <param name="publishing"></param>
    /// <param name="topicName"></param>
    /// <returns></returns>
    public static MqttSubscriberConfiguration ToMqttTopic(this IPublishToExpression publishing, string topicName)
    {
        var transports = publishing.As<PublishingExpression>().Parent.Transports;
        var transport = transports.GetOrCreate<MqttTransport>();

        var topic = transport.Topics[topicName];

        // This is necessary unfortunately to hook up the subscription rules
        publishing.To(topic.Uri);

        return new MqttSubscriberConfiguration(topic);
    }

    /// <summary>
    /// Publish messages to an MQTT topic on an additional, named broker registered via
    /// <see cref="AddNamedMqttBroker(WolverineOptions, BrokerName, Action{ManagedMqttClientOptionsBuilder}, MqttJwtAuthenticationOptions)"/>.
    /// </summary>
    /// <param name="publishing"></param>
    /// <param name="name">Identity of the additional MQTT broker</param>
    /// <param name="topicName">The MQTT topic name</param>
    public static MqttSubscriberConfiguration ToMqttTopicOnNamedBroker(this IPublishToExpression publishing,
        BrokerName name, string topicName)
    {
        var transports = publishing.As<PublishingExpression>().Parent.Transports;
        var transport = transports.GetOrCreate<MqttTransport>(name);

        var topic = transport.Topics[topicName];

        // This is necessary unfortunately to hook up the subscription rules
        publishing.To(topic.Uri);

        return new MqttSubscriberConfiguration(topic);
    }

    /// <summary>
    /// Publish messages to MQTT topics based on Wolverine's rules for deriving topic
    /// names from a message type
    /// </summary>
    /// <param name="publishing"></param>
    /// <param name="topicName"></param>
    /// <returns></returns>
    public static MqttSubscriberConfiguration ToMqttTopics(this IPublishToExpression publishing)
    {
        var transports = publishing.As<PublishingExpression>().Parent.Transports;
        var transport = transports.GetOrCreate<MqttTransport>();

        var topic = transport.Topics[MqttTopic.WolverineTopicsName];

        // This is necessary unfortunately to hook up the subscription rules
        publishing.To(topic.Uri);

        return new MqttSubscriberConfiguration(topic);
    }

    /// <summary>
    /// Publish messages that are of type T or could be cast to type T to an MQTT
    /// topic using the supplied function to determine the topic for the message
    /// </summary>
    /// <param name="options"></param>
    /// <param name="topicSource"></param>
    /// <typeparam name="T"></typeparam>
    /// <returns></returns>
    public static MqttSubscriberConfiguration PublishMessagesToMqttTopic<T>(this WolverineOptions options,
        Func<T, string> topicSource)
    {
        return options.PublishMessagesToMqttTopic(topicSource, additive: false);
    }

    /// <summary>
    /// Publish messages that are of type T or could be cast to type T to an MQTT
    /// topic using the supplied function to determine the topic for the message,
    /// choosing whether Wolverine's other routing rules still apply to the same messages
    /// </summary>
    /// <param name="options"></param>
    /// <param name="topicSource"></param>
    /// <param name="additive">
    /// When true, the MQTT route is added to whatever else would route the message: an explicit rule
    /// such as <c>PublishMessage&lt;T&gt;().ToLocalQueue("name")</c>, or local routing to a handler for
    /// <typeparamref name="T"/> in this application, still applies, so one publish goes to the MQTT topic
    /// and to the local queue at the same time. When false -- the behavior of the overload without this
    /// parameter -- the MQTT topic is the only route those terminating rules leave the message.
    /// </param>
    /// <typeparam name="T"></typeparam>
    /// <returns></returns>
    public static MqttSubscriberConfiguration PublishMessagesToMqttTopic<T>(this WolverineOptions options,
        Func<T, string> topicSource, bool additive)
    {
        var transports = options.Transports;
        var transport = transports.GetOrCreate<MqttTransport>();

        var topic = transport.NewTopicSender();
        var routing = new TopicRouting<T>(topicSource, topic) { IsPassThrough = additive };
        options.PublishWithMessageRoutingSource(routing);

        return new MqttSubscriberConfiguration(topic);
    }
}