using System.Diagnostics;
using Wolverine;
using Wolverine.Http;

namespace WolverineWebApi;

public record HttpMessage1(string Name);
public record HttpMessage2(string Name);
public record HttpMessage3(string Name);
public record HttpMessage4(string Name);
public record HttpMessage5(string Name);
public record HttpMessage6(string Name);

public record CustomRequest(string Name);
public record CustomResponse(string Name);

public static class MessageHandler
{
    public static CustomResponse Handle(CustomRequest request) => new CustomResponse(request.Name);
    
    public static void Handle(HttpMessage1 message) => Debug.WriteLine("Got message 1");
    public static void Handle(HttpMessage2 message) => Debug.WriteLine("Got message 2");
    public static void Handle(HttpMessage3 message) => Debug.WriteLine("Got message 3");
    public static void Handle(HttpMessage4 message) => Debug.WriteLine("Got message 4");
    public static void Handle(HttpMessage5 message) => Debug.WriteLine("Got message 5");
    public static void Handle(HttpMessage6 message) => Debug.WriteLine("Got message 6");
}

public record SpawnInput(string Name);

public static class MessageSpawnerEndpoint
{
    #region sample_publishing_cascading_messages_from_Http_endpoint_with_IMessageBus
    
    // This would have an empty response and a 204 status code
    [WolverinePost("/spawn3")]
    public static async ValueTask SendViaMessageBus(IMessageBus bus)
    {
        await bus.PublishAsync(new HttpMessage1("foo"));
        await bus.PublishAsync(new HttpMessage2("bar"));
    }

    #endregion
    
    #region sample_publishing_cascading_messages_from_Http_endpoint

    // This would have an empty response and a 204 status code
    [EmptyResponse]
    [WolverinePost("/spawn2")]
    public static (HttpMessage1, HttpMessage2) Post()
    {
        return new(new HttpMessage1("foo"), new HttpMessage2("bar"));
    }

    #endregion

    #region sample_spawning_messages_from_http_endpoint_via_OutgoingMessages

    // This would have a string response and a 200 status code
    [WolverinePost("/spawn")]
    public static (string, OutgoingMessages) Post(SpawnInput input)
    {
        var messages = new OutgoingMessages
        {
            new HttpMessage1(input.Name),
            new HttpMessage2(input.Name),
            new HttpMessage3(input.Name),
            new HttpMessage4(input.Name)
        };

        return ("got it", messages);
    }

    #endregion
}