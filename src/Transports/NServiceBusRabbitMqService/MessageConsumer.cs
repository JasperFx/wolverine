using NServiceBus;

namespace NServiceBusService;

public class MessageConsumer : IHandleMessages<ToExternal>
{
    public static List<ToExternal> Received = new();

    private static TaskCompletionSource<ToExternal> _completion = new();

    public static Task WaitForReceipt()
    {
        return _completion.Task.WaitAsync(TimeSpan.FromSeconds(60));
    }

    public Task Handle(ToExternal message, IMessageHandlerContext context)
    {
        Received.Add(message);

        _completion.SetResult(message);
        return Task.CompletedTask;
    }
}
