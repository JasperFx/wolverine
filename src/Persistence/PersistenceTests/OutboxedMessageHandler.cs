using System;
using System.Threading.Tasks;
using JasperFx.Core;

namespace PersistenceTests;

public record OutboxedMessage
{
    public Guid Id { get; set; }
}

public class OutboxedMessageHandler
{
    private static TaskCompletionSource<OutboxedMessage> _source;

    public static Task<OutboxedMessage> WaitForNextMessage()
    {
        _source = new TaskCompletionSource<OutboxedMessage>();

        return _source.Task.WaitAsync(15.Seconds());
    }

    public void Handle(OutboxedMessage message)
    {
        _source?.TrySetResult(message);
    }
}