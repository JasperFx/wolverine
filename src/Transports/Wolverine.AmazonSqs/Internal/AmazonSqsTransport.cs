using Amazon.Runtime;
using Amazon.SQS;
using Amazon.SQS.Model;
using Baseline;
using Microsoft.Extensions.Hosting;
using Oakton.Resources;
using Wolverine.Configuration;
using Wolverine.Runtime;
using Wolverine.Transports;

namespace Wolverine.AmazonSqs.Internal;

internal class AmazonSqsTransport : TransportBase<AmazonSqsEndpoint>
{
    public AmazonSqsTransport() : base("sqs", "Amazon SQS")
    {
        Queues = new(name => new AmazonSqsEndpoint(name, this));
    }

    internal AmazonSqsTransport(IAmazonSQS client) : this()
    {
        Client = client;
    }

    public Func<IWolverineRuntime, AWSCredentials>? CredentialSource { get; set; }

    public LightweightCache<string, AmazonSqsEndpoint> Queues { get; }

    public AmazonSQSConfig Config { get; } = new();
    public bool AutoProvision { get; set; }
    public bool AutoPurgeOnStartup { get; set; }

    protected override IEnumerable<AmazonSqsEndpoint> endpoints()
    {
        return Queues;
    }

    protected override AmazonSqsEndpoint findEndpointByUri(Uri uri)
    {
        if (uri.Scheme != Protocol) throw new ArgumentOutOfRangeException(nameof(uri));

        return Queues[uri.Host];
    }

    public override async ValueTask InitializeAsync(IWolverineRuntime runtime)
    {
        if (UseLocalStackInDevelopment && runtime.Environment.IsDevelopment())
        {
        }
        
        Client ??= BuildClient(runtime);

        foreach (var endpoint in Queues)
        {
            await endpoint.InitializeAsync();
        }
    }

    public IAmazonSQS BuildClient(IWolverineRuntime runtime)
    {
        if (CredentialSource == null)
        {
            return new AmazonSQSClient(Config);
        }

        var credentials = CredentialSource(runtime);
        return new AmazonSQSClient(credentials, Config);
    }

    internal AmazonSqsEndpoint EndpointForQueue(string queueName)
    {
        return Queues[queueName];
    }

    internal IAmazonSQS? Client { get; private set; }
    

    public int LocalStackPort { get; set; }

    public bool UseLocalStackInDevelopment { get; set; }

    public override bool TryBuildStatefulResource(IWolverineRuntime runtime, out IStatefulResource resource)
    {
        resource = new AmazonSqsTransportStatefulResource(this, runtime);
        return true;
    }

    public void ConnectToLocalStack(int port = 4566)
    {
        CredentialSource = _ => new BasicAWSCredentials("ignore", "ignore");
        Config.ServiceURL = $"http://localhost:{port}";
    }
}

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
        _transport.AutoPurgeOnStartup = true;
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
        var policy = new LambdaEndpointPolicy<AmazonSqsEndpoint>((e, runtime) =>
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
        var policy = new LambdaEndpointPolicy<AmazonSqsEndpoint>((e, runtime) =>
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