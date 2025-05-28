using Microsoft.Extensions.Logging;

namespace MartenTests.Bugs.ConsumerFeature2;

public class MyEventConsumer
{
    // once a second consumer like this is present the issue appears
    public static void Consume(MyEvent @event,
        ILogger<MyEventConsumer> logger)
    {
        logger.LogInformation("consumer 2 processing: {Id}", @event.Id);
    }
}