using System.Diagnostics;
using Wolverine;
using Wolverine.Http;

namespace WolverineWebApi.Bugs;

public class ConvertBookEndpoint
{
    [WolverinePost("/convert-book")]
    public static async Task Post(
        TelegramUpdated @event,
        IMessageBus bus,
        CancellationToken token)
    {
        await bus.InvokeAsync(@event, token);
    }
}

public record TelegramUpdated(string Name);

public static class TelegramUpdatedHandler
{
    public static void Handle(TelegramUpdated updated)
    {
        Debug.WriteLine("Got TelegramUpdated with name " + updated.Name);
    }
}