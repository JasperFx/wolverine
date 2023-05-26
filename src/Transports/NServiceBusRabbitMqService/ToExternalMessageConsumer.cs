using NServiceBus;

namespace NServiceBusRabbitMqService;

public class ToExternalMessageConsumer : IHandleMessages<ToExternal>
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

public class ToExternalInterfaceMessageConsumer : IHandleMessages<IToExternalMessage>
{
    public static List<IToExternalMessage> Received = new();

    private static readonly TaskCompletionSource<IToExternalMessage> _completion = new();

    public Task Handle(IToExternalMessage message, IMessageHandlerContext context)
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

public interface IToExternalMessage
{
    Guid Id { get; set; }
}