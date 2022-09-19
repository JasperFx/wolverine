using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Baseline;
using Wolverine.Configuration;
using Wolverine.Runtime;

namespace Wolverine.Transports.Local;

public class LocalTransport : TransportBase<LocalQueueSettings>
{
    private readonly Cache<string, LocalQueueSettings> _queues;
    
    public LocalTransport() : base(TransportConstants.Local, "Local (In Memory)")
    {
        _queues = new(name => new LocalQueueSettings(name));

        _queues.FillDefault(TransportConstants.Default);
        _queues.FillDefault(TransportConstants.Replies);

        _queues[TransportConstants.Durable].Mode = EndpointMode.Durable;
    }

    protected override IEnumerable<LocalQueueSettings> endpoints()
    {
        return _queues;
    }

    protected override LocalQueueSettings findEndpointByUri(Uri uri)
    {
        var queueName = QueueName(uri);
        var settings = _queues[queueName];

        return settings;
    }

    public override ValueTask InitializeAsync(IWolverineRuntime runtime)
    {
        return ValueTask.CompletedTask;
    }

    public override Endpoint ReplyEndpoint()
    {
        return _queues[TransportConstants.Replies];
    }


    public IEnumerable<LocalQueueSettings> AllQueues()
    {
        return _queues;
    }
    
    /// <summary>
    ///     Retrieves a local queue by name
    /// </summary>
    /// <param name="queueName"></param>
    /// <returns></returns>
    public LocalQueueSettings QueueFor(string queueName)
    {
        return _queues[queueName.ToLowerInvariant()];
    }

    public static string QueueName(Uri uri)
    {
        if (uri == TransportConstants.LocalUri)
        {
            return TransportConstants.Default;
        }

        if (uri == TransportConstants.DurableLocalUri)
        {
            return TransportConstants.Durable;
        }

        if (uri.Scheme == TransportConstants.Local)
        {
            return uri.Host;
        }

        var lastSegment = uri.Segments.Skip(1).LastOrDefault();

        return lastSegment ?? TransportConstants.Default;
    }

    public static Uri AtQueue(Uri uri, string queueName)
    {
        if (queueName.IsEmpty())
        {
            return uri;
        }

        if (uri.Scheme == TransportConstants.Local && uri.Host != TransportConstants.Durable)
        {
            return new Uri("local://" + queueName);
        }

        return new Uri(uri, queueName);
    }

}
