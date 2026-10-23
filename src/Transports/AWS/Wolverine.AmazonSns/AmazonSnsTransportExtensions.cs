using Amazon.SimpleNotificationService;
using Amazon.SQS;
using JasperFx.Core.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Wolverine.AmazonSns.Internal;
using Wolverine.AmazonSqs.Internal;
using Wolverine.Configuration;

namespace Wolverine.AmazonSns;

public static class AmazonSnsTransportExtensions
{
    /// <summary>
    ///     Quick access to the Amazon SNS Transport within this application.
    ///     This is for advanced usage
    /// </summary>
    /// <param name="endpoints"></param>
    /// <returns></returns>
    internal static AmazonSnsTransport AmazonSnsTransport(this WolverineOptions endpoints, BrokerName? name = null)
    {
        var transports = endpoints.As<WolverineOptions>().Transports;

        var transport = transports.GetOrCreate<AmazonSnsTransport>(name);

        if (name == null)
        {
            // Default broker: pair the SNS-side SQS client + subscription-queue provisioning with the shared
            // default SQS transport so both target the same account/region and share the user's SQS config.
            transport.SQS ??= transports.GetOrCreate<AmazonSqsTransport>();
        }
        else if (transport.SQS == null)
        {
            // Named broker (GH-3305): the SNS transport holds this broker's Protocol key (== the name), so its
            // topic URIs are "{name}://topic". A paired SQS transport registered under the SAME key would evict the
            // SNS from the TransportCollection (which keys transports by Protocol). Since the paired SQS is only used
            // internally for the SNS-side client + SNS->SQS subscription provisioning (never as a registered
            // listener/sender surface), give it a standalone, unregistered transport that ConnectAsync seeds from the
            // SNS connection so it targets the same account/region. A user who also wants to LISTEN on the
            // subscribed queue does so through a separate (default or differently-named) SQS broker.
            transport.SQS = new AmazonSqsTransport();
            transport.PairedSqsIsStandalone = true;
        }

        return transport;
    }

    /// <summary>
    /// Apply additive configuration to the AWS SNS integration within this Wolverine application
    /// </summary>
    /// <param name="options"></param>
    /// <returns></returns>
    public static AmazonSnsTransportConfiguration ConfigureAmazonSnsTransport(this WolverineOptions options)
    {
        var transport = options.AmazonSnsTransport();
        return new AmazonSnsTransportConfiguration(transport, options);
    }
    
    public static AmazonSnsTransportConfiguration UseAmazonSnsTransport(this WolverineOptions options)
    {
        var transport = options.AmazonSnsTransport();
        return new AmazonSnsTransportConfiguration(transport, options);
    }

    public static AmazonSnsTransportConfiguration UseAmazonSnsTransport(this WolverineOptions options,
        Action<AmazonSimpleNotificationServiceConfig> configuration)
    {
        var transport = options.AmazonSnsTransport();
        configuration(transport.SnsConfig);
        return new AmazonSnsTransportConfiguration(transport, options);
    }
    
    public static AmazonSnsTransportConfiguration UseAmazonSnsTransport(this WolverineOptions options,
        Action<AmazonSimpleNotificationServiceConfig, AmazonSQSConfig> configuration)
    {
        var transport = options.AmazonSnsTransport();
        configuration(transport.SnsConfig, transport.SQS.Config);
        return new AmazonSnsTransportConfiguration(transport, options);
    }

    /// <summary>
    /// Direct Wolverine to connect to Amazon SNS through the service URL held in the named connection string
    /// from IConfiguration. This option is useful for Aspire integration between Wolverine and a local AWS
    /// emulator: the LocalStack (<c>AddLocalStack</c>) and CommunityToolkit Floci (<c>AddFlociAws</c>) hosting
    /// integrations put the emulator's service URL (e.g. <c>http://localhost:4566</c>) at
    /// <c>ConnectionStrings:{name}</c>, and Wolverine applies it as the SNS
    /// <see cref="AmazonSimpleNotificationServiceConfig.ServiceURL"/> -- and to the paired SQS config used to
    /// provision topic subscriptions, unless that already has its own ServiceURL. Unless you configure
    /// credentials explicitly, the same placeholder credentials as <c>UseAmazonSnsTransportLocally()</c> are used.
    /// <para>
    /// Real AWS has no connection string under Aspire (<c>Aspire.Hosting.AWS</c> passes the profile and region
    /// as the <c>AWS_PROFILE</c>/<c>AWS_REGION</c> environment variables that the AWS SDK already reads), so
    /// use <c>UseAmazonSnsTransport()</c> or <see cref="UseAmazonSnsTransportWithClientsFromContainer"/> for that.
    /// </para>
    /// </summary>
    /// <param name="options"></param>
    /// <param name="connectionStringName">Key of the expected connection string from IConfiguration</param>
    /// <param name="configuration">Optional extra configuration of the AmazonSimpleNotificationServiceConfig</param>
    /// <returns></returns>
    public static AmazonSnsTransportConfiguration UseAmazonSnsTransportUsingNamedConnection(this WolverineOptions options,
        string connectionStringName, Action<AmazonSimpleNotificationServiceConfig>? configuration = null)
    {
        var transport = options.AmazonSnsTransport();
        configuration?.Invoke(transport.SnsConfig);
        return options.applyNamedConnection(transport, connectionStringName);
    }

    /// <summary>
    /// Direct Wolverine to connect to Amazon SNS through the service URL held in the named connection string
    /// from IConfiguration, with extra configuration of both the SNS config and the paired SQS config. See
    /// <see cref="UseAmazonSnsTransportUsingNamedConnection(WolverineOptions, string, Action{AmazonSimpleNotificationServiceConfig})"/>
    /// </summary>
    /// <param name="options"></param>
    /// <param name="connectionStringName">Key of the expected connection string from IConfiguration</param>
    /// <param name="configuration">Extra configuration of the SNS config and the paired SQS config</param>
    /// <returns></returns>
    public static AmazonSnsTransportConfiguration UseAmazonSnsTransportUsingNamedConnection(this WolverineOptions options,
        string connectionStringName, Action<AmazonSimpleNotificationServiceConfig, AmazonSQSConfig> configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var transport = options.AmazonSnsTransport();
        configuration(transport.SnsConfig, transport.SQS.Config);
        return options.applyNamedConnection(transport, connectionStringName);
    }

    /// <summary>
    /// Add an additional, named broker connection to Amazon SNS through the service URL held in the named
    /// connection string from IConfiguration. See
    /// <see cref="UseAmazonSnsTransportUsingNamedConnection(WolverineOptions, string, Action{AmazonSimpleNotificationServiceConfig})"/>
    /// </summary>
    /// <param name="options"></param>
    /// <param name="name">The name of the additional broker</param>
    /// <param name="connectionStringName">Key of the expected connection string from IConfiguration</param>
    /// <param name="configuration">Optional extra configuration of the AmazonSimpleNotificationServiceConfig</param>
    /// <returns></returns>
    public static AmazonSnsTransportConfiguration AddNamedAmazonSnsBrokerUsingNamedConnection(this WolverineOptions options,
        BrokerName name, string connectionStringName, Action<AmazonSimpleNotificationServiceConfig>? configuration = null)
    {
        var transport = options.AmazonSnsTransport(name);
        configuration?.Invoke(transport.SnsConfig);
        return options.applyNamedConnection(transport, connectionStringName);
    }

    private static AmazonSnsTransportConfiguration applyNamedConnection(this WolverineOptions options,
        AmazonSnsTransport transport, string connectionStringName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionStringName);

        // The SNS + paired SQS clients are only built from the configs in ConnectAsync, after this callback
        options.ApplyNamedConnectionString("Amazon SNS", connectionStringName, "AddLocalStack",
            value => transport.ApplyNamedServiceUrl(connectionStringName, value));

        return new AmazonSnsTransportConfiguration(transport, options);
    }

    /// <summary>
    /// Use the <see cref="IAmazonSimpleNotificationService"/> client registered in the application's IoC
    /// container -- for example by <c>services.AddAWSService&lt;IAmazonSimpleNotificationService&gt;()</c> from
    /// AWSSDK.Extensions.NETCore.Setup -- rather than having Wolverine build one from its own config. The SQS
    /// client Wolverine uses to provision SNS-to-SQS subscriptions is likewise taken from the container when an
    /// <see cref="IAmazonSQS"/> is registered there, and otherwise built from the paired SQS config. Clients are
    /// resolved when the transport connects, and Wolverine does not dispose them.
    /// </summary>
    /// <param name="options"></param>
    /// <param name="snsClientSource">
    /// Optional way to pick the SNS client out of the container, e.g. a keyed service. By default this resolves
    /// <see cref="IAmazonSimpleNotificationService"/>
    /// </param>
    /// <param name="sqsClientSource">
    /// Optional way to pick the SQS client out of the container. By default this resolves
    /// <see cref="IAmazonSQS"/> if one is registered. Returning null falls back to building one.
    /// </param>
    /// <returns></returns>
    public static AmazonSnsTransportConfiguration UseAmazonSnsTransportWithClientsFromContainer(this WolverineOptions options,
        Func<IServiceProvider, IAmazonSimpleNotificationService>? snsClientSource = null,
        Func<IServiceProvider, IAmazonSQS?>? sqsClientSource = null)
    {
        var transport = options.AmazonSnsTransport();
        return options.useClientsFromContainer(transport, snsClientSource, sqsClientSource);
    }

    /// <summary>
    /// Add an additional, named broker connection to Amazon SNS that uses clients from the application's IoC
    /// container. See <see cref="UseAmazonSnsTransportWithClientsFromContainer"/>
    /// </summary>
    /// <param name="options"></param>
    /// <param name="name">The name of the additional broker</param>
    /// <param name="snsClientSource">Picks this broker's SNS client out of the container, e.g. a keyed service</param>
    /// <param name="sqsClientSource">
    /// Optional way to pick this broker's SQS client (for subscription provisioning) out of the container.
    /// When omitted or returning null, Wolverine builds one aligned with the SNS client's configuration.
    /// </param>
    /// <returns></returns>
    public static AmazonSnsTransportConfiguration AddNamedAmazonSnsBrokerWithClientsFromContainer(this WolverineOptions options,
        BrokerName name, Func<IServiceProvider, IAmazonSimpleNotificationService> snsClientSource,
        Func<IServiceProvider, IAmazonSQS?>? sqsClientSource = null)
    {
        ArgumentNullException.ThrowIfNull(snsClientSource);
        var transport = options.AmazonSnsTransport(name);

        // A named broker's paired SQS is standalone, so don't grab the container's default IAmazonSQS unasked
        sqsClientSource ??= _ => null;
        return options.useClientsFromContainer(transport, snsClientSource, sqsClientSource);
    }

    private static AmazonSnsTransportConfiguration useClientsFromContainer(this WolverineOptions options,
        AmazonSnsTransport transport, Func<IServiceProvider, IAmazonSimpleNotificationService>? snsClientSource,
        Func<IServiceProvider, IAmazonSQS?>? sqsClientSource)
    {
        snsClientSource ??= services => services.GetRequiredService<IAmazonSimpleNotificationService>();
        sqsClientSource ??= services => services.GetService<IAmazonSQS>();

        transport.SnsClientSource = runtime => snsClientSource(runtime.Services);
        transport.SqsClientSource = runtime => sqsClientSource(runtime.Services);

        return new AmazonSnsTransportConfiguration(transport, options);
    }

    /// <summary>
    ///     Sets up a connection to a locally running Amazon SNS LocalStack
    ///     broker for development or testing purposes
    /// </summary>
    /// <param name="port">Port for SNS. Default is 4566</param>
    /// <returns></returns>
    public static AmazonSnsTransportConfiguration UseAmazonSnsTransportLocally(this WolverineOptions options,
        int port = 4566)
    {
        var transport = options.AmazonSnsTransport();

        transport.ConnectToLocalStack(port);

        return new AmazonSnsTransportConfiguration(transport, options);
    }

    /// <summary>
    /// Add an additional, named broker connection to Amazon SNS
    /// </summary>
    /// <param name="options"></param>
    /// <param name="name"></param>
    /// <returns></returns>
    public static AmazonSnsTransportConfiguration AddNamedAmazonSnsBroker(this WolverineOptions options, BrokerName name)
    {
        var transport = options.AmazonSnsTransport(name);
        return new AmazonSnsTransportConfiguration(transport, options);
    }

    /// <summary>
    /// Add an additional, named broker connection to Amazon SNS
    /// </summary>
    /// <param name="options"></param>
    /// <param name="name"></param>
    /// <param name="configuration"></param>
    /// <returns></returns>
    public static AmazonSnsTransportConfiguration AddNamedAmazonSnsBroker(this WolverineOptions options, BrokerName name,
        Action<AmazonSimpleNotificationServiceConfig> configuration)
    {
        var transport = options.AmazonSnsTransport(name);
        configuration(transport.SnsConfig);
        return new AmazonSnsTransportConfiguration(transport, options);
    }

    /// <summary>
    ///     Sets up a connection to a locally running Amazon SNS LocalStack broker as a named broker
    ///     for development or testing purposes
    /// </summary>
    /// <param name="name">The name of the additional broker</param>
    /// <param name="port">Port for SNS. Default is 4566</param>
    /// <returns></returns>
    public static AmazonSnsTransportConfiguration UseAmazonSnsTransportLocallyAsNamedBroker(this WolverineOptions options,
        BrokerName name, int port = 4566)
    {
        var transport = options.AmazonSnsTransport(name);

        transport.ConnectToLocalStack(port);

        return new AmazonSnsTransportConfiguration(transport, options);
    }

    /// <summary>
    ///     Publish matching messages to AWS SNS using the topic name
    /// </summary>
    /// <param name="publishing"></param>
    /// <param name="topicName">The name of the AWS SNS topic</param>
    /// <returns></returns>
    public static AmazonSnsSubscriberConfiguration ToSnsTopic(this IPublishToExpression publishing, string topicName)
    {
        var transports = publishing.As<PublishingExpression>().Parent.Transports;
        var transport = transports.GetOrCreate<AmazonSnsTransport>();

        var corrected = transport.MaybeCorrectName(topicName);

        var endpoint = transport.EndpointForTopic(corrected);
        endpoint.EndpointName = topicName;

        // This is necessary unfortunately to hook up the subscription rules
        publishing.To(endpoint.Uri);

        return new AmazonSnsSubscriberConfiguration(endpoint);
    }

    /// <summary>
    ///     Publish matching messages to AWS SNS using the topic name on a specific, named broker
    /// </summary>
    /// <param name="publishing"></param>
    /// <param name="name">The name of the additional Amazon SNS broker</param>
    /// <param name="topicName">The name of the AWS SNS topic</param>
    /// <returns></returns>
    public static AmazonSnsSubscriberConfiguration ToSnsTopicOnNamedBroker(this IPublishToExpression publishing,
        BrokerName name, string topicName)
    {
        var transport = publishing.As<PublishingExpression>().Parent.AmazonSnsTransport(name);

        var corrected = transport.MaybeCorrectName(topicName);

        var endpoint = transport.EndpointForTopic(corrected);
        endpoint.EndpointName = topicName;

        // This is necessary unfortunately to hook up the subscription rules
        publishing.To(endpoint.Uri);

        return new AmazonSnsSubscriberConfiguration(endpoint);
    }
}
