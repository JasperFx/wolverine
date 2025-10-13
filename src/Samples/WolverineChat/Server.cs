using JasperFx.Core;
using Wolverine;
using Wolverine.Runtime;

namespace WolverineChat;

#region sample_WolverineChatWebSocketMessage

// Marker interface for the sample application just to facilitate
// message routing
public interface WolverineChatWebSocketMessage : WebSocketMessage;

#endregion

#region sample_signalr_message_types

public record ChatMessage(string User, string Text) : WolverineChatWebSocketMessage;
public record ResponseMessage(string User, string Text) : WolverineChatWebSocketMessage;

public record Ping(int Number) : WolverineChatWebSocketMessage;

#endregion

public static class ChatMessageHandler
{
    public static ResponseMessage Handle(ChatMessage message) 
        => new ResponseMessage(message.User, message.Text);
}



#region sample_signalr_pinging

public class Pinging : BackgroundService
{
    private readonly IWolverineRuntime _runtime;

    public Pinging(IWolverineRuntime runtime)
    {
        _runtime = runtime;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var number = 0;
        while (!stoppingToken.IsCancellationRequested)
        {
            await Task.Delay(1.Seconds(), stoppingToken);
            
            // This is being published to all connected SignalR
            // applications
            await new MessageBus(_runtime).PublishAsync(new Ping(++number));
        }
    }
}

#endregion