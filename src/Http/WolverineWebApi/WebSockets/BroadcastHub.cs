using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks.Dataflow;
using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Frames;
using Lamar;
using Microsoft.AspNetCore.SignalR;
using Wolverine.Configuration;
using Wolverine.Runtime.Handlers;
using Wolverine.Util.Dataflow;

namespace WolverineWebApi.WebSockets;

// Setting this up for usage with Redux style
// state management on the client side
public interface IClientMessage
{
    [JsonPropertyName("type")]
    public string TypeName => GetType().Name;
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

public class Broadcaster : IDisposable
{
    private readonly BroadcastHub _hub;
    private readonly ActionBlock<IClientMessage[]> _publishing;
    private readonly BatchingBlock<IClientMessage> _batching;

    public Broadcaster(BroadcastHub hub)
    {
        _hub = hub;
        _publishing = new ActionBlock<IClientMessage[]>(messages => _hub.SendBatchAsync(messages),
            new ExecutionDataflowBlockOptions
            {
                EnsureOrdered = true,
                MaxDegreeOfParallelism = 1
            });

        // BatchingBlock is a Wolverine internal building block that's
        // purposely public for this kind of usage.
        // This will do the "debounce" for us
        _batching = new BatchingBlock<IClientMessage>(250, _publishing);
    }

    public void Dispose()
    {
        _hub.Dispose();
        _batching.Dispose();
    }

    public Task Post(IClientMessage? message)
    {
        return message is null or NoClientMessage
            ? Task.CompletedTask
            : _batching.SendAsync(message);
    }

    public async Task PostMany(IEnumerable<IClientMessage> messages)
    {
        foreach (var message in messages.Where(x => x != null))
        {
            if (message is NoClientMessage) continue;

            await _batching.SendAsync(message);
        }
    }
}

public class BroadcastClientMessages : IChainPolicy
{
    public void Apply(IReadOnlyList<IChain> chains, GenerationRules rules, IContainer container)
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
    public static int Count = 0;

    // We're trying to teach Wolverine to send CountUpdated
    // return values via WebSockets instead of async
    // message routing
    public static CountUpdated Handle(IncrementCount command)
    {
        Count++;
        return new CountUpdated(Count);
    }
}