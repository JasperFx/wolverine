using System.Text.Json;
using Microsoft.Extensions.Logging;
using Wolverine.Configuration;
using Wolverine.Runtime;
using Wolverine.Transports;
using Wolverine.Transports.Sending;

namespace Wolverine.Http.Transport;

public class HttpEndpoint : Endpoint
{
    public HttpEndpoint(Uri uri, EndpointRole role) : base(uri, role)
    {
    }

    internal bool SupportsNativeScheduledSend { get; set; }
    public string OutboundUri { get; set; }

    public override ValueTask<IListener> BuildListenerAsync(IWolverineRuntime runtime, IReceiver receiver)
    {
        return ValueTask.FromResult<IListener>(new NulloListener(Uri));
    }

    public JsonSerializerOptions SerializerOptions { get; set; } = new JsonSerializerOptions
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };
    protected override ISender CreateSender(IWolverineRuntime runtime)
    {
        return Mode == EndpointMode.Inline
            ? new InlineHttpSender(this, runtime, runtime.Services)
            : new BatchedSender(
                this,
                new HttpSenderProtocol(this, runtime.Services),
                runtime.Cancellation,
                runtime.LoggerFactory.CreateLogger<HttpSenderProtocol>());
    }

    public override IDictionary<string, object> DescribeProperties()
    {
        return base.DescribeProperties();
    }

    protected override bool supportsMode(EndpointMode mode)
    {
        return true;
    }
}

