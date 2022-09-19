using System;
using Wolverine.Util;
using Wolverine.Configuration;
using Wolverine.Runtime;
using Wolverine.Transports.Sending;

namespace Wolverine.Transports.Local;

public class LocalQueueSettings : Endpoint
{
    public LocalQueueSettings(string name)
    {
        Name = name.ToLowerInvariant();
    }

    public LocalQueueSettings(Uri uri) : base(uri)
    {
    }

    public override Uri Uri => $"local://{Name}".ToUri();

    public override void Parse(Uri uri)
    {
        Name = LocalTransport.QueueName(uri);
        Mode = EndpointMode.BufferedInMemory;
    }

    public override Uri CorrectedUriForReplies()
    {
        return Mode == EndpointMode.Durable ? $"local://durable/{Name}".ToUri() : $"local://{Name}".ToUri();
    }

    public override IListener BuildListener(IWolverineRuntime runtime, IReceiver receiver)
    {
        throw new NotSupportedException();
    }

    protected override ISender CreateSender(IWolverineRuntime runtime)
    {
        throw new NotSupportedException();
    }

    public override string ToString()
    {
        return $"Local Queue '{Name}'";
    }
}
