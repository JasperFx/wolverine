using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NSubstitute;
using Shouldly;
using Wolverine.RabbitMQ.Internal;
using Wolverine.Runtime;
using Wolverine.Transports;
using Xunit;

namespace Wolverine.RabbitMQ.Tests.Bugs;

/// <summary>
/// GH-3842. RabbitMqChannelAgent.EnsureInitiated() is best-effort: it returns without a channel when the
/// agent has been disposed, and it logs-and-swallows a failure to open one. CreateAsync() used to call
/// `Queue.DeclareAsync(Channel!, Logger)` straight afterwards, so both outcomes surfaced as a bare
/// NullReferenceException from RabbitMqQueue.DeclareAsync -- six frames from the actual cause.
///
/// This was the mechanism behind the intermittent failure of
/// Bug_189_fails_if_there_are_many_messages_in_queue_on_startup under concurrent host lifecycle.
/// </summary>
public class Bug_3842_listener_create_with_no_channel : IAsyncLifetime
{
    private IHost _host = null!;
    private RabbitMqTransport _transport = null!;
    private RabbitMqQueue _queue = null!;

    public async ValueTask InitializeAsync()
    {
        var queueName = "gh3842-" + Guid.NewGuid().ToString("n").Substring(0, 8);

        _host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.UseRabbitMq().AutoProvision();
                opts.ListenToRabbitQueue(queueName);
            }).StartAsync();

        var runtime = _host.Services.GetRequiredService<IWolverineRuntime>();
        _transport = runtime.Options.Transports.GetOrCreate<RabbitMqTransport>();
        _queue = _transport.Queues[queueName];
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await _host.StopAsync();
        }
        catch (Exception)
        {
            // One of these tests deliberately disposes the transport's listening connection, so an
            // orderly shutdown is not always possible afterwards. The assertion has already run.
        }

        _host.Dispose();
    }

    private RabbitMqListener buildListener()
    {
        var runtime = _host.Services.GetRequiredService<IWolverineRuntime>();
        return new RabbitMqListener(runtime, _queue, _transport, Substitute.For<IReceiver>());
    }

    [Fact]
    public async Task disposed_during_startup_abandons_creation_quietly()
    {
        var listener = buildListener();

        // The race as it happens in the field: the host stops while this listener is still coming up,
        // so EnsureInitiated() returns early and never opens a channel.
        await listener.DisposeAsync();
        listener.Channel.ShouldBeNull();

        // Previously threw NullReferenceException from RabbitMqQueue.DeclareAsync.
        await Should.NotThrowAsync(() => listener.CreateAsync());
    }

    [Fact]
    public async Task a_live_agent_that_cannot_open_a_channel_throws_something_diagnosable()
    {
        // Take the connection out from under the transport so that startNewChannel() genuinely fails.
        // EnsureInitiated() logs and swallows that, then returns with Channel still null and the agent
        // very much alive -- the second of its two no-channel exits, and the one that is an error.
        //
        // Note it is not enough to just null out Channel: EnsureInitiated() would simply open a fresh
        // one and the branch under test would never be reached.
        await _transport.ListeningConnection.DisposeAsync();

        var listener = buildListener();
        await listener.EnsureInitiated();
        listener.Channel.ShouldBeNull();
        listener.IsDisposed.ShouldBeFalse();

        var ex = await Should.ThrowAsync<InvalidOperationException>(() => listener.CreateAsync());

        // The message has to name the endpoint and the queue. The whole complaint in GH-3842 is that a
        // bare NullReferenceException six frames deep told you neither.
        ex.Message.ShouldContain(_queue.QueueName);
        ex.Message.ShouldContain("Unable to open a Rabbit MQ channel");

        await listener.DisposeAsync();
    }
}
