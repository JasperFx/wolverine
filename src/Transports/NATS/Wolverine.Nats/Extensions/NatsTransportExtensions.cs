using JasperFx.Core.Reflection;
using Microsoft.Extensions.Configuration;
using Wolverine.Configuration;
using Wolverine.Nats.Configuration;
using Wolverine.Nats.Internal;

namespace Wolverine.Nats;

public static class NatsTransportExtensions
{
    /// <summary>
    /// Get access to the NATS transport for advanced configuration
    /// </summary>
    internal static NatsTransport NatsTransport(this WolverineOptions options)
    {
        return options.Transports.GetOrCreate<NatsTransport>();
    }

    /// <summary>
    /// Configure Wolverine to use NATS as a message transport
    /// </summary>
    public static NatsTransportExpression UseNats(
        this WolverineOptions options,
        string connectionString = "nats://localhost:4222"
    )
    {
        var transport = options.NatsTransport();
        transport.Configuration.ConnectionString = connectionString;
        return new NatsTransportExpression(transport, options);
    }

    /// <summary>
    /// Configure Wolverine to use NATS as a message transport with custom configuration
    /// </summary>
    public static NatsTransportExpression UseNats(
        this WolverineOptions options,
        Action<NatsTransportConfiguration> configure
    )
    {
        var transport = options.NatsTransport();
        configure(transport.Configuration);
        return new NatsTransportExpression(transport, options);
    }

    /// <summary>
    /// Configure Wolverine to use NATS as a message transport using IConfiguration
    /// Reads configuration from "Wolverine:Nats" section
    /// </summary>
    public static NatsTransportExpression UseNats(
        this WolverineOptions options,
        IConfiguration configuration
    )
    {
        var transport = options.NatsTransport();

        // First try to bind from Wolverine:Nats section (proper nested configuration)
        var wolverineSection = configuration.GetSection("Wolverine:Nats");
        if (wolverineSection.Exists())
        {
            wolverineSection.Bind(transport.Configuration);
        }

        // Fall back to root-level Nats section for backward compatibility
        var natsSection = configuration.GetSection("Nats");
        if (natsSection.Exists() && !wolverineSection.Exists())
        {
            natsSection.Bind(transport.Configuration);
        }

        // Override with environment variable if set (for containers/cloud deployments)
        var envUrl = Environment.GetEnvironmentVariable("NATS_URL");
        if (!string.IsNullOrEmpty(envUrl))
        {
            transport.Configuration.ConnectionString = envUrl;
        }

        return new NatsTransportExpression(transport, options);
    }

    /// <summary>
    /// Publish messages to a NATS subject
    /// </summary>
    public static NatsSubscriberConfiguration ToNatsSubject(
        this IPublishToExpression publishing,
        string subject
    )
    {
        var transports = publishing.As<PublishingExpression>().Parent.Transports;
        var transport = transports.GetOrCreate<NatsTransport>();

        var endpoint = transport.EndpointForSubject(subject);

        // This is necessary to hook up the subscription rules
        publishing.To(endpoint.Uri);

        return new NatsSubscriberConfiguration(endpoint);
    }

    /// <summary>
    /// Publish messages to a NATS subject
    /// </summary>
    public static NatsSubscriberConfiguration PublishToNatsSubject<T>(
        this WolverineOptions options,
        string subject
    )
    {
        var transport = options.NatsTransport();
        var endpoint = transport.EndpointForSubject(subject);

        options.PublishMessage<T>().To(endpoint.Uri);

        return new NatsSubscriberConfiguration(endpoint);
    }

    /// <summary>
    /// Listen to messages from a NATS subject
    /// </summary>
    public static NatsListenerConfiguration ListenToNatsSubject(
        this WolverineOptions options,
        string subject
    )
    {
        var transport = options.NatsTransport();
        var endpoint = transport.EndpointForSubject(subject);
        endpoint.IsListener = true;

        return new NatsListenerConfiguration(endpoint);
    }

    /// <summary>
    /// Access the NATS transport configuration for advanced scenarios.
    /// This is useful for adding policies or modifying configuration after initial setup.
    /// </summary>
    public static NatsTransportExpression ConfigureNats(this WolverineOptions options)
    {
        var transport = options.NatsTransport();
        return new NatsTransportExpression(transport, options);
    }
}
