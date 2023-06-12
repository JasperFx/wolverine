using TestingSupport.Compliance;
using Wolverine;

namespace ConsoleApp;

public class PingHandler
{
    public void Ping(Envelope envelope, PingMessage message)
    {
    }
}

public class PongHandler
{
    public Task Handle(PongMessage message)
    {
        return Task.CompletedTask;
    }
}