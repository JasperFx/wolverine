#region sample_ponghandler
using Messages;

namespace Pinger;

public class PongHandler
{
    public void Handle(Pong pong, ILogger<PongHandler> logger)
    {
        logger.LogInformation("Received Pong #{Number}", pong.Number);
    }
}

#endregion