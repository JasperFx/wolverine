using System.Text.Json;
using JasperFx.Descriptors;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Wolverine.Configuration;
using Wolverine.Runtime;
using Wolverine.Runtime.RemoteInvocation;
using Wolverine.Runtime.Serialization;
using Wolverine.Transports;
using Wolverine.Transports.Sending;

namespace Wolverine.Http.Transport;

public class HttpEndpoint : Endpoint, IInlineRequestReplyEndpoint
{
    public HttpEndpoint(Uri uri, EndpointRole role) : base(uri, role)
    {
        BrokerRole = "route";
    }

    // GH-2966: HTTP carries the reply in the same response, so InvokeAsync<T> reads the reply envelope
    // straight off the HTTP response body — no ReplyListener, no listening endpoint on the sender.
    public async Task<Envelope> InvokeRemoteAsync(Envelope request, IWolverineRuntime runtime, CancellationToken cancellation)
    {
        if (request.Data == null && request.Message != null)
        {
            request.Serializer ??= DefaultSerializer;
            request.Data = request.Serializer!.Write(request);
            request.ContentType ??= request.Serializer!.ContentType;
        }

        using var scope = runtime.Services.CreateScope();
        var client = scope.ServiceProvider.GetRequiredService<IWolverineHttpTransportClient>()
                     ?? throw new InvalidOperationException(
                         "IWolverineHttpTransportClient is not registered in the service container");

        var reply = await client.InvokeAsync(OutboundUri, request, SerializerOptions);

        if (reply.Body is { Length: > 0 })
        {
            return EnvelopeSerializer.Deserialize(reply.Body);
        }

        // Receiver returned no envelope body (infrastructure error); surface as a failure reply so the
        // caller gets the usual WolverineRequestReplyException.
        var ack = new FailureAcknowledgement
        {
            RequestId = request.Id,
            Message =
                $"Inline HTTP request/reply to {OutboundUri} failed with status code {reply.StatusCode} and no reply body"
        };
        return new Envelope { Message = ack };
    }

    internal bool SupportsNativeScheduledSend { get; set; }
    public string OutboundUri { get; set; } = string.Empty;

    public override ValueTask<IListener> BuildListenerAsync(IWolverineRuntime runtime, IReceiver receiver)
    {
        return ValueTask.FromResult<IListener>(new NulloListener(Uri));
    }

    [IgnoreDescription]
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
                    runtime.LoggerFactory.CreateLogger<HttpSenderProtocol>())
                { SupportsNativeScheduledSend = SupportsNativeScheduledSend };
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

