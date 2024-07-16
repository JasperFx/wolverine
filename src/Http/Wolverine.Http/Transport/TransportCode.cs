using System.Diagnostics;
using System.Net.Http.Headers;
using JasperFx.Core;
using JasperFx.Core.Reflection;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Oakton.Resources;
using Wolverine.Configuration;
using Wolverine.ErrorHandling;
using Wolverine.Runtime;
using Wolverine.Runtime.Handlers;
using Wolverine.Runtime.Serialization;
using Wolverine.Runtime.WorkerQueues;
using Wolverine.Transports;
using Wolverine.Transports.Sending;
using Endpoint = Wolverine.Configuration.Endpoint;

namespace Wolverine.Http.Transport;

public static class HttpTransportExtensions
{
    public static RouteGroupBuilder MapWolverineHttpTransportEndpoints(this IEndpointRouteBuilder endpoints, string groupUrlPrefix = "/_wolverine")
    {
        var group = endpoints.MapGroup(groupUrlPrefix);
        
        group.MapPost(
            "/batch/{queue}",(HttpContext c, HttpTransportExecutor executor) => executor.ExecuteBatchAsync(c));

        group.MapPost("/invoke", (HttpContext c, HttpTransportExecutor executor) => executor.InvokeAsync(c));

        return group;
    }

    public static HttpTransportSubscriberConfiguration ToHttpEndpoint(this IPublishToExpression publishing, string url)
    {
        var transports = publishing.As<PublishingExpression>().Parent.Transports;
        var transport = transports.GetOrCreate<HttpTransport>();

        var endpoint = transport.EndpointFor(url);

        // This is necessary unfortunately to hook up the subscription rules
        publishing.To(endpoint.Uri);

        return new HttpTransportSubscriberConfiguration(endpoint);
    }
}

public class HttpTransportSubscriberConfiguration : SubscriberConfiguration<HttpTransportSubscriberConfiguration, HttpEndpoint>
{
    internal HttpTransportSubscriberConfiguration(HttpEndpoint endpoint) : base(endpoint)
    {
    }
}

internal class HttpTransportExecutor
{
    private readonly WolverineRuntime _runtime;

    public HttpTransportExecutor(IWolverineRuntime runtime)
    {
        _runtime = (WolverineRuntime)runtime;
    }

    public async Task ExecuteBatchAsync(HttpContext httpContext)
    {
        var data = await httpContext.Request.Body.ReadAllBytesAsync();
        httpContext.Request.RouteValues.TryGetValue("queue", out var raw);
        
        // TODO -- validate that the content-type is "binary/wolverine-envelope"
        
        // TODO -- harden around this
        var envelopes = EnvelopeSerializer.ReadMany(data);

        var queueName = raw as string ?? TransportConstants.Default;
        var queue = (ILocalQueue)_runtime.Endpoints.AgentForLocalQueue(queueName);

        var nulloListener = new NulloListener($"http://localhost{httpContext.Request.Path}".ToUri());
        await queue.ReceivedAsync(nulloListener, envelopes.ToArray());

        httpContext.Response.StatusCode = 200;
    }

    public async Task InvokeAsync(HttpContext httpContext)
    {
        // TODO -- validate that the content-type is "binary/wolverine-envelope"
        var data = await httpContext.Request.Body.ReadAllBytesAsync();
        var envelope = EnvelopeSerializer.Deserialize(data);
        envelope.Destination = $"http://localhost{httpContext.Request.Path}".ToUri();
        envelope.DoNotCascadeResponse = true;
        envelope.Serializer = _runtime.Options.FindSerializer(envelope.ContentType);

        // TODO -- try catch this one for serialization errors
        if (!_runtime.Pipeline.TryDeserializeEnvelope(envelope, out var continuation))
        {
            if (continuation is NoHandlerContinuation)
            {
                // TODO -- set an error here
            }
        }

        if (envelope.ReplyRequested.IsNotEmpty())
        {
            if (_runtime.Handlers.TryFindMessageType(envelope.ReplyRequested, out var responseType))
            {
                envelope.ResponseType = responseType;
            }
            else
            {
                // TODO -- need to set status code here with ProblemDetails
            }
        }
        
        
        // TODO -- this can error out
        var executor = _runtime.FindInvoker(envelope.MessageType) as Executor;
        // TODO -- assert when does not exist

        await executor.InvokeInlineAsync(envelope, httpContext.RequestAborted);

        if (envelope.Response != null)
        {
            var response = envelope.CreateForResponse(envelope.Response);
            if (response.Serializer == null)
            {
                response.Serializer = _runtime.Options.DefaultSerializer;
                response.ContentType = response.Serializer.ContentType;
            }

            response.Data = response.Serializer.WriteMessage(response.Message);

            httpContext.Response.ContentType = "binary/wolverine-envelope";
            var responseData = EnvelopeSerializer.Serialize(response);
            httpContext.Response.ContentLength = responseData.Length;
            await httpContext.Response.Body.WriteAsync(responseData);
        }
        
        // TODO -- need to log message failed too
        
        _runtime.MessageTracking.MessageSucceeded(envelope);
    }
}

internal struct NulloChannelCallback : IChannelCallback
{
    public ValueTask CompleteAsync(Envelope envelope)
    {
        return new ValueTask();
    }

    public ValueTask DeferAsync(Envelope envelope)
    {
        return new ValueTask();
    }
}

internal class HttpTransport : ITransport
{
    public static readonly string EnvelopeContentType = "binary/wolverine-envelope";
    public static readonly string EnvelopesContentType = "binary/wolverine-envelopes";
    
    private readonly Cache<Uri, HttpEndpoint> _endpoints 
        = new(uri => new HttpEndpoint(uri, EndpointRole.Application));
    
    public string Protocol { get; } = "http";
    public string Name { get; } = "Http Transport";
    public Endpoint? ReplyEndpoint()
    {
        throw new NotImplementedException();
    }

    public Endpoint GetOrCreateEndpoint(Uri uri)
    {
        throw new NotImplementedException();
    }

    public Endpoint? TryGetEndpoint(Uri uri)
    {
        throw new NotImplementedException();
    }

    public IEnumerable<Endpoint> Endpoints()
    {
        throw new NotImplementedException();
    }

    public async ValueTask InitializeAsync(IWolverineRuntime runtime)
    {
        throw new NotImplementedException();
    }

    public bool TryBuildStatefulResource(IWolverineRuntime runtime, out IStatefulResource? resource)
    {
        throw new NotImplementedException();
    }

    public HttpEndpoint EndpointFor(string url)
    {
        throw new NotImplementedException();
    }
}

public class HttpEndpoint : Endpoint
{
    public HttpEndpoint(Uri uri, EndpointRole role) : base(uri, role)
    {
    }

    public string OutboundUri { get; set; }

    public override ValueTask<IListener> BuildListenerAsync(IWolverineRuntime runtime, IReceiver receiver)
    {
        return ValueTask.FromResult<IListener>(new NulloListener(Uri));
    }

    protected override ISender CreateSender(IWolverineRuntime runtime)
    {
        return new BatchedSender(
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
        return mode != EndpointMode.Inline;
    }
}

internal class WolverineHttpTransportClient : HttpClient
{
    public async Task SendBatchAsync(string uri, OutgoingMessageBatch batch)
    {
        var content = new ByteArrayContent(EnvelopeSerializer.Serialize(batch.Messages));
        content.Headers.ContentType = new MediaTypeHeaderValue("binary/wolverine-envelope");
        await PostAsync(uri, content);
    }
}

internal class HttpSenderProtocol : ISenderProtocol
{
    private readonly HttpEndpoint _endpoint;
    private readonly IServiceProvider _services;
    private readonly IHttpClientFactory _clientFactory;

    public HttpSenderProtocol(HttpEndpoint endpoint, IServiceProvider services)
    {
        _endpoint = endpoint;
        _services = services;
    }

    public async Task SendBatchAsync(ISenderCallback callback, OutgoingMessageBatch batch)
    {
        using var scope = _services.CreateScope();
        var client = scope.ServiceProvider.GetRequiredService<WolverineHttpTransportClient>();
        await client.SendBatchAsync(_endpoint.OutboundUri, batch);
    }
}

internal class NulloListener : IListener
{
    public NulloListener(Uri address)
    {
        Address = address;
    }

    public ValueTask CompleteAsync(Envelope envelope)
    {
        return new ValueTask();
    }

    public ValueTask DeferAsync(Envelope envelope)
    {
        return new ValueTask();
    }

    public ValueTask DisposeAsync()
    {
        return new ValueTask();
    }

    public Uri Address { get; }
    public ValueTask StopAsync()
    {
        return new ValueTask();
    }
}