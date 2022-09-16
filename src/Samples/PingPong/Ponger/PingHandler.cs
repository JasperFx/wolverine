#region sample_PingHandler

using Wolverine;
using Messages;
using Microsoft.Extensions.Logging;

namespace Ponger;

public class PingHandler
{
    public ValueTask Handle(Ping ping, ILogger<PingHandler> logger, IMessageContext context)
    {
        logger.LogInformation("Got Ping #{Number}", ping.Number);
        return context.RespondToSenderAsync(new Pong { Number = ping.Number });
    }
}


#endregion
