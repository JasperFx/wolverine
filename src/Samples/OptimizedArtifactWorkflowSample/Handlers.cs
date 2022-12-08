using JasperFx.Core;
using Microsoft.Extensions.Hosting;
using Spectre.Console;
using Wolverine;

public class MessageSender : BackgroundService
{
    private readonly IMessageBus _bus;

    public MessageSender(IMessageBus bus)
    {
        _bus = bus;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var count = 0;
        while (!stoppingToken.IsCancellationRequested)
        {
            await Task.Delay(500.Milliseconds(), stoppingToken).ConfigureAwait(false);
            await _bus.PublishAsync(new TrackedMessage { Number = ++count }).ConfigureAwait(false);
        }
    }
}

public class TrackedMessage
{
    public int Number { get; set; }
}

public class TrackedMessageHandler
{
    public void Handle(TrackedMessage message)
    {
        AnsiConsole.MarkupLine($"[green]Got message {message.Number}[/]");
    }
}