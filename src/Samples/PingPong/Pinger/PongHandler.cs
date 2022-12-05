#region sample_PongHandler

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