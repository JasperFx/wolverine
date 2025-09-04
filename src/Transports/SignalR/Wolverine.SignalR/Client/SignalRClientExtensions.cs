using System.Text.Json;
using JasperFx.Core.Reflection;
using Wolverine.Configuration;

namespace Wolverine.SignalR.Client;

public static class SignalRClientExtensions
{
    /// <summary>
    /// Add a SignalR Client based transport to this Wolverine. This transport option
    /// was meant for end to end testing with Wolverine.SignalR, but can be used
    /// for production usage as well
    /// </summary>
    /// <param name="options"></param>
    /// <param name="url"></param>
    /// <param name="jsonOptions"></param>
    /// <returns></returns>
    public static Uri UseSignalRClient(this WolverineOptions options, string url,
        JsonSerializerOptions? jsonOptions = null)
    {
        var transport = options.Transports.GetOrCreate<SignalRClientTransport>();
        
        var endpoint = transport.ForClientUrl(url);
        if (jsonOptions != null)
        {
            endpoint.JsonOptions = jsonOptions;
        }

        return endpoint.Uri;
    }

    /// <summary>
    /// Send a message via a SignalR Client for the given server Uri in the format "http://localhost:[port]/[hub url]"
    /// </summary>
    /// <param name="bus"></param>
    /// <param name="serverUri"></param>
    /// <param name="message"></param>
    /// <returns></returns>
    public static ValueTask SendViaSignalRClient(this IMessageBus bus, Uri serverUri, object message)
    {
        var wolverineUri = SignalRClientEndpoint.TranslateToWolverineUri(serverUri);
        return bus.EndpointFor(wolverineUri).SendAsync(message);
    }
    
    /// <summary>
    /// Send a message via a SignalR Client for the given server Uri in the format "http://localhost:[port]/[hub url]"
    /// </summary>
    /// <param name="bus"></param>
    /// <param name="serverUri"></param>
    /// <param name="message"></param>
    /// <returns></returns>
    public static ValueTask SendViaSignalRClient(this IMessageBus bus, string serverUrl, object message)
    {
        var wolverineUri = SignalRClientEndpoint.TranslateToWolverineUri(new Uri(serverUrl));
        return bus.EndpointFor(wolverineUri).SendAsync(message);
    }

    /// <summary>
    /// Route messages via a SignalR Client pointed at the localhost, port, and relativeUrl
    /// </summary>
    /// <param name="publishing"></param>
    /// <param name="port"></param>
    /// <param name="relativeUrl"></param>
    public static void ToSignalRWithClient(this IPublishToExpression publishing, int port, string relativeUrl)
    {
        var url = $"http://localhost:{port}/{relativeUrl}";
        publishing.ToSignalRWithClient(url);
    }
    
    /// <summary>
    /// Route messages via a SignalR Client pointed at the supplied absolute Url
    /// </summary>
    /// <param name="publishing"></param>
    /// <param name="url"></param>
    public static void ToSignalRWithClient(this IPublishToExpression publishing, string url)
    {
        var rawUri = new Uri(url);
        if (!rawUri.IsAbsoluteUri)
        {
            throw new ArgumentOutOfRangeException(nameof(url), "Must be an absolute Url");
        }
        
        var uri = SignalRClientEndpoint.TranslateToWolverineUri(rawUri);
        var transports = publishing.As<PublishingExpression>().Parent.Transports;
        var transport = transports.GetOrCreate<SignalRClientTransport>();

        var endpoint = transport.Clients[uri];
        publishing.To(uri);
    }
    
    
}