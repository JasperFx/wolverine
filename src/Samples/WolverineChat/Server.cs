using JasperFx.Core;
using Wolverine;
using Wolverine.Runtime;

namespace WolverineChat;

public record ChatMessage(string User, string Text) : WebSocketMessage;
public record ResponseMessage(string User, string Text) : WebSocketMessage;

public static class ChatMessageHandler
{
    public static ResponseMessage Handle(ChatMessage message) => new ResponseMessage(message.User, message.Text);
}

public record Ping(int Number) : WebSocketMessage;

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
            await new MessageBus(_runtime).PublishAsync(new Ping(++number));
        }
    }
}