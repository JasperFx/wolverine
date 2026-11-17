using JasperFx.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Shouldly;
using Wolverine.Configuration;
using Wolverine.RabbitMQ.Internal;
using Wolverine.Tracking;
using Xunit;

namespace Wolverine.RabbitMQ.Tests.Bugs;

/// <summary>
/// The first send on a RabbitMqSender opens its channel under the agent's lock, and EnsureInitiated() only set
/// State = Connected after startNewChannel() had already assigned the open channel and logged. A concurrent send
/// in that window passed the lock free "channel is open" check, found the agent still Disconnected and threw
/// "The RabbitMQ agent for ... is disconnected", which the sending agent counts towards its circuit breaker.
/// </summary>
public class Bug_5627_concurrent_sends_while_the_sender_channel_opens : IAsyncLifetime
{
    private readonly string _queueName = "sender_opening_" + Guid.NewGuid().ToString("N")[..8];
    private HoldTheChannelOpening _opening = null!;
    private IHost _host = null!;

    public async ValueTask InitializeAsync()
    {
        _opening = new HoldTheChannelOpening(_queueName);

        _host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.UseRabbitMq().AutoProvision();
                opts.Services.AddSingleton<ILoggerProvider>(_opening);
            }).StartAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await _host.StopAsync();
        _host.Dispose();
    }

    [Fact]
    public async Task a_send_while_the_first_send_opens_the_channel_does_not_fail()
    {
        var token = TestContext.Current.CancellationToken;
        var runtime = _host.GetRuntime();
        var transport = runtime.Options.Transports.GetOrCreate<RabbitMqTransport>();
        await using var sender = new RabbitMqSender(transport.Queues[_queueName], transport, RoutingMode.Static, runtime);

        // The first send opens the channel and is held between assigning it and setting State = Connected
        var first = Task.Run(() => sender.SendAsync(Envelope.ForPing(sender.Destination)).AsTask(), token);
        await _opening.Opening.Task.WaitAsync(10.Seconds(), token);

        // Used to throw "The RabbitMQ agent for ... is disconnected"
        await Should.NotThrowAsync(() => sender.SendAsync(Envelope.ForPing(sender.Destination)).AsTask());
        await first;

        sender.State.ShouldBe(AgentState.Connected);
    }
}

/// <summary>
/// Holds the sender for one queue inside its "Opened a new channel" log call, which runs after Channel is
/// assigned and before State is set, so that a second send lands in that window every time
/// </summary>
public class HoldTheChannelOpening(string queueName) : ILoggerProvider
{
    public TaskCompletionSource Opening { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public ILogger CreateLogger(string categoryName) => new Holder(Opening, queueName);

    public void Dispose()
    {
    }

    private class Holder(TaskCompletionSource opening, string queueName) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            var message = formatter(state, exception);
            if (!message.StartsWith("Opened a new channel") || !message.Contains(queueName)) return;

            opening.TrySetResult();
            Thread.Sleep(1.Seconds());
        }
    }
}
