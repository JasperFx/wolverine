using System.Text.Json;
using System.Text.Json.Serialization;
using Humanizer;
using JasperFx;
using JasperFx.Blocks;
using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Frames;
using Microsoft.AspNetCore.SignalR;
using Wolverine.Configuration;
using Wolverine.Runtime;
using Wolverine.Runtime.Handlers;

namespace WolverineWebApi.WebSockets;

// Setting this up for usage with Redux style
// state management on the client side
public interface IClientMessage
{
    [JsonPropertyName("type")] public string TypeName => GetType().Name;
}

// This is just a "nullo" message that might
// be useful to mean "don't send anything in this case"
public record NoClientMessage : IClientMessage;

public class BroadcastHub : Hub
{
    public Task SendBatchAsync(IClientMessage[] messages)
    {
        return Clients.All.SendAsync("Updates", JsonSerializer.Serialize(messages));
    }
}

public class Broadcaster : IAsyncDisposable
{
    private readonly IBlock<IClientMessage> _batching;

    public Broadcaster()
    {
        var publishing = new Block<IClientMessage[]>(async (messages, _) =>
        {
            using var hub = new BroadcastHub();
            await hub.SendBatchAsync(messages);
        });

        _batching = publishing.BatchUpstream(250.Milliseconds());
    }

    public async ValueTask DisposeAsync()
    {
        await _batching.DisposeAsync();
    }

    public ValueTask Post(IClientMessage? message)
    {
        return message is null or NoClientMessage
            ? new ValueTask()
            : _batching.PostAsync(message);
    }

    public async Task PostMany(IEnumerable<IClientMessage> messages)
    {
        foreach (var message in messages.Where(x => x != null))
        {
            if (message is NoClientMessage)
            {
                continue;
            }

            await _batching.PostAsync(message);
        }
    }
}

public class BroadcastClientMessages : IChainPolicy
{
    public void Apply(IReadOnlyList<IChain> chains, GenerationRules rules, IServiceContainer container)
    {
        // We're going to look through all known message handler and HTTP endpoint chains
        // and see where there's any return values of IClientMessage or IEnumerable<IClientMessage>
        // and apply our custom return value handling
        foreach (var chain in chains)
        {
            foreach (var message in chain.ReturnVariablesOfType<IClientMessage>())
            {
                message.UseReturnAction(v =>
                {
                    var call = MethodCall.For<Broadcaster>(x => x.Post(null!));
                    call.Arguments[0] = message;

                    return call;
                });
            }

            foreach (var messages in chain.ReturnVariablesOfType<IEnumerable<IClientMessage>>())
            {
                messages.UseReturnAction(v =>
                {
                    var call = MethodCall.For<Broadcaster>(x => x.PostMany(null!));
                    call.Arguments[0] = messages;

                    return call;
                });
            }
        }
    }
}

public record CountUpdated(int Value) : IClientMessage;

public record IncrementCount;

public static class SomeUpdateHandler
{
    public static int Count;

    // We're trying to teach Wolverine to send CountUpdated
    // return values via WebSockets instead of async
    // message routing
    public static CountUpdated Handle(IncrementCount command)
    {
        Count++;
        return new CountUpdated(Count);
    }
}