using NServiceBus;

namespace NServiceBusRabbitMqService;

public class MessageConsumer : IHandleMessages<ToExternal>
{
    public static List<ToExternal> Received = new();

    private static readonly TaskCompletionSource<ToExternal> _completion = new();

    public Task Handle(ToExternal message, IMessageHandlerContext context)
    {
        Received.Add(message);

        _completion.SetResult(message);
        return Task.CompletedTask;
    }

    public static Task WaitForReceipt()
    {
        return _completion.Task.WaitAsync(TimeSpan.FromSeconds(60));
    }
}