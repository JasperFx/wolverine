using Amazon.Runtime;
using Baseline;
using Wolverine.Configuration;
using Wolverine.Runtime;

namespace Wolverine.AmazonSqs.Internal;

internal class AmazonSqlTransportConfiguration : IAmazonSqsTransportConfiguration
{
    private readonly AmazonSqsTransport _transport;
    private readonly WolverineOptions _options;

    public AmazonSqlTransportConfiguration(AmazonSqsTransport transport, WolverineOptions options)
    {
        _transport = transport;
        _options = options;
    }

    public IAmazonSqsTransportConfiguration Credentials(AWSCredentials credentials)
    {
        _transport.CredentialSource = r => credentials;
        return this;
    }

    public IAmazonSqsTransportConfiguration Credentials(Func<IWolverineRuntime, AWSCredentials> credentialSource)
    {
        _transport.CredentialSource = credentialSource;
        return this;
    }

    public IAmazonSqsTransportConfiguration AutoProvision()
    {
        _transport.AutoProvision = true;
        return this;
    }

    public IAmazonSqsTransportConfiguration AutoPurgeOnStartup()
    {
        _transport.AutoPurgeAllQueues = true;
        return this;
    }

    public IAmazonSqsTransportConfiguration UseLocalStackIfDevelopment(int port = 4566)
    {
        _transport.LocalStackPort = port;
        _transport.UseLocalStackInDevelopment = true;
        return this;
    }

    public IAmazonSqsTransportConfiguration ConfigureListeners(Action<AmazonSqsListenerConfiguration> configure)
    {
        var policy = new LambdaEndpointPolicy<AmazonSqsQueue>((e, runtime) =>
        {
            if (e.Role == EndpointRole.System) return;
            if (!e.IsListener) return;

            var configuration = new AmazonSqsListenerConfiguration(e);
            configure(configuration);

            configuration.As<IDelayedEndpointConfiguration>().Apply();
        });
        
        _options.Policies.Add(policy);

        return this;
    }

    public IAmazonSqsTransportConfiguration ConfigureSenders(Action<AmazonSqsSubscriberConfiguration> configure)
    {
        var policy = new LambdaEndpointPolicy<AmazonSqsQueue>((e, runtime) =>
        {
            if (e.Role == EndpointRole.System) return;
            if (!e.Subscriptions.Any()) return;

            var configuration = new AmazonSqsSubscriberConfiguration(e);
            configure(configuration);

            configuration.As<IDelayedEndpointConfiguration>().Apply();
        });
        
        _options.Policies.Add(policy);

        return this;
    }

    public IAmazonSqsTransportConfiguration UseConventionalRouting(Action<AmazonSqsMessageRoutingConvention>? configure = null)
    {
        var routing = new AmazonSqsMessageRoutingConvention();
        configure?.Invoke(routing);
        
        _options.RouteWith(routing);
        
        return this;
    }
}