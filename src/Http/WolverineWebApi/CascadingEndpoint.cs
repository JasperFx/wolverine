using System.Diagnostics;
using Marten;
using Wolverine;
using Wolverine.Attributes;
using Wolverine.Http;

namespace WolverineWebApi;

public static class CascadingEndpoint
{
    public static OutgoingMessages Before(string name)
    {
        return [new BeforeMessage1(name)];
    }
    
    [Transactional]
    [WolverinePost("/middleware-messages/{name}")]
    public static string Post(string name, IDocumentSession session)
    {
        return "Hey";
    }

    public static async Task After(IMessageBus bus, string name)
    {
        await bus.PublishAsync(new AfterMessage1(name));
    }
}

public record BeforeMessage1(string Name);
public record AfterMessage1(string Name);

public static class MiddlewareMessageHandler
{
    public static void Handle(BeforeMessage1 message) => Debug.WriteLine("Got " + message);
    public static void Handle(AfterMessage1 message) => Debug.WriteLine("Got " + message);
}