using DotPulsar;
using JasperFx.Core;
using Microsoft.Extensions.Logging;
using Wolverine.Configuration;
using Wolverine.Runtime;
using Wolverine.Transports;
using Wolverine.Transports.Sending;

namespace Wolverine.Pulsar;

public class PulsarEndpoint : Endpoint
{
    public const string Persistent = "persistent";
    public const string NonPersistent = "non-persistent";
    public const string DefaultNamespace = "tenant";
    public const string Public = "public";
    private readonly PulsarTransport _parent;

    public PulsarEndpoint(Uri uri, PulsarTransport parent) : base(uri, EndpointRole.Application)
    {
        _parent = parent;
        Parse(uri);
    }

    public string Persistence { get; private set; } = Persistent;
    public string Tenant { get; private set; } = Public;
    public string Namespace { get; private set; } = DefaultNamespace;
    public string? TopicName { get; private set; }
    public string SubscriptionName { get; internal set; } = "Wolverine";
    public SubscriptionType SubscriptionType { get; internal set; } = SubscriptionType.Exclusive;

    /// <summary>
    ///     Use to override the dead letter topic for this endpoint
    /// </summary>
    public DeadLetterTopic? DeadLetterTopic { get; set; }

    /// <summary>
    ///     Use to override the retry letter topic for this endpoint
    /// </summary>
    public RetryLetterTopic? RetryLetterTopic { get; set; }

    public bool IsPersistent => Persistence.Equals(Persistent);

    public static Uri UriFor(bool persistent, string tenant, string @namespace, string topicName)
    {
        var scheme = persistent ? "persistent" : "non-persistent";
        return new Uri($"{scheme}://{tenant}/{@namespace}/{topicName}");
    }

    public static Uri UriFor(string topicPath)
    {
        var uri = new Uri(topicPath);
        return new Uri(
            $"pulsar://{uri.Scheme}/{uri.Host}/{uri.Segments.Skip(1).Select(x => x.TrimEnd('/')).Join("/")}");
    }

    public override IDictionary<string, object> DescribeProperties()
    {
        var dict = base.DescribeProperties();

        dict.Add(nameof(Persistent), Persistent);
        dict.Add(nameof(Tenant), Tenant);
        dict.Add(nameof(Namespace), Namespace);
        if (TopicName != null)
        {
            dict.Add(nameof(TopicName), TopicName);
        }

        return dict;
    }

    internal PulsarEnvelopeMapper BuildMapper(IWolverineRuntime runtime)
    {
        return new PulsarEnvelopeMapper(this, runtime);
    }

    internal void Parse(Uri uri)
    {
        if (uri.Segments.Length != 4)
        {
            throw new InvalidPulsarUriException(uri);
        }

        if (uri.Host != Persistent && uri.Host != NonPersistent)
        {
            throw new InvalidPulsarUriException(uri);
        }

        Persistence = uri.Host;
        Tenant = uri.Segments[1].TrimEnd('/');
        Namespace = uri.Segments[2].TrimEnd('/');
        TopicName = uri.Segments[3].TrimEnd('/');
    }

    public string PulsarTopic()
    {
        return $"{Persistence}://{Tenant}/{Namespace}/{TopicName}";
    }

    public override ValueTask<IListener> BuildListenerAsync(IWolverineRuntime runtime, IReceiver receiver)
    {
        var listener = new PulsarListener(runtime, this, receiver, _parent, runtime.Cancellation);
        return ValueTask.FromResult<IListener>(listener);
    }

    protected override ISender CreateSender(IWolverineRuntime runtime)
    {
        return new PulsarSender(runtime, this, _parent, runtime.Cancellation);
    }

    public override ValueTask InitializeAsync(ILogger logger)
    {
        return base.InitializeAsync(logger);
    }

    public override bool TryBuildDeadLetterSender(IWolverineRuntime runtime, out ISender? deadLetterSender)
    {
        return base.TryBuildDeadLetterSender(runtime, out deadLetterSender);


        // TODO: ?
        //var queueName =  this.DeadLetterTopic?.TopicName ?? _parent.DeadLetterTopic.TopicName;
        //var dlq = _parent[UriFor(queueName)];
        //deadLetterSender = dlq.CreateSender(runtime);
        //return true;
    }
}