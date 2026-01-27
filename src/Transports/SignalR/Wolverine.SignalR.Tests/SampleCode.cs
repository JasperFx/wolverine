using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Wolverine.SignalR.Client;

namespace Wolverine.SignalR.Tests;

public class SampleCode
{
    public async Task configure_json()
    {
        #region sample_overriding_signalr_serialization

        var builder = WebApplication.CreateBuilder();

        builder.UseWolverine(opts =>
        {
            // Just showing you how to override the JSON serialization
            opts.UseSignalR().OverrideJson(new JsonSerializerOptions
            {
                IgnoreReadOnlyProperties = false
            });
        });

        #endregion
    }

    public static async Task configure_signalr_client()
    {
        #region sample_bootstrap_signalr_client_for_realsies

        var builder = Host.CreateApplicationBuilder();
        builder.UseWolverine(opts =>
        {
            // this would need to be an absolute Url to where SignalR is
            // hosted on your application and include the exact route where
            // the WolverineHub is listening
            var url = builder.Configuration.GetValue<string>("signalr.url");
            opts.UseClientToSignalR(url);

            // Setting this up to publish any messages implementing
            // the WebSocketMessage marker interface with the SignalR
            // client
            opts.Publish(x =>
            {
                x.MessagesImplementing<WebSocketMessage>();
                x.ToSignalRWithClient(url);
            });
        });

        #endregion
        
    }
    
    public static async Task configure_signalr_client_locally()
    {
        #region sample_bootstrap_signalr_client_for_local

        // Ostensibly, *something* in your test harness would 
        // be telling you the port number of the real application
        int port = 5555;

        using var clientHost = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                // Just so you know it's possible, you can override
                // the relative url of the SignalR WolverineHub route
                // in the hosting application
                opts.UseClientToSignalR(port, "/api/messages");

                // Setting this up to publish any messages implementing
                // the WebSocketMessage marker interface with the SignalR
                // client
                opts.Publish(x =>
                {
                    x.MessagesImplementing<WebSocketMessage>();
                    x.ToSignalRWithClient(port);
                });
            }).StartAsync();

        #endregion
        
    }
}

#region sample_sending_response_to_originating_signalr_caller

public record RequestSum(int X, int Y) : WebSocketMessage;
public record SumAnswer(int Value) : WebSocketMessage;

public static class RequestSumHandler
{
    public static ResponseToCallingWebSocket<SumAnswer> Handle(RequestSum message)
    {
        return new SumAnswer(message.X + message.Y)
            
            // This extension method will wrap the raw message
            // with some helpers that will 
            .RespondToCallingWebSocket();
    }
}

#endregion



