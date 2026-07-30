using JasperFx.Core;
using JasperFx.Resources;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using RabbitMQ.Client;
using Shouldly;
using Wolverine.RabbitMQ.Internal;
using Wolverine.Runtime;
using Wolverine.Tracking;
using Xunit;

namespace Wolverine.RabbitMQ.Tests.Bugs;

/// <summary>
/// GH-3706: every ack used to go out as <c>BasicAckAsync(tag, multiple: true)</c>, which tells the broker
/// "and every lower delivery tag on this channel too". That is only correct when completions happen in
/// delivery order, and they do not — out-of-order completion already exists today with
/// <c>ConsumerDispatchConcurrency</c> above 1 — so acking tag N silently swept up deliveries whose handlers
/// were still running. A crash at that moment is silent message loss.
///
/// The cumulative sweep was load-bearing, which is why flipping it to per-message in isolation during the
/// GH-3492 perf wave leaked a message into the quorum queues and was reverted: two dead-letter paths posted
/// a copy elsewhere and never settled the original delivery at all, and nothing but a later cumulative ack
/// reclaimed them.
///
/// These tests pin the settle. A delivery that has gone through a dead-letter path must leave the source
/// queue empty even after the channel closes — an unsettled delivery is requeued by the broker on channel
/// close, which is exactly how the leak manifested.
/// </summary>
public class Bug_3706_dead_letter_paths_settle_the_original_delivery : IAsyncLifetime
{
    private readonly string _queueName = "gh3706_" + Guid.NewGuid().ToString("N")[..8];
    private string DeadLetterQueueName => _queueName + "_DLQ";
    private IHost _host = null!;

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public async ValueTask DisposeAsync()
    {
        if (_host != null!)
        {
            await _host.StopAsync(TestContext.Current.CancellationToken);
            await _host.TeardownResources();
            _host.Dispose();
        }
    }

    private async Task<RabbitMqTransport> bootstrapAsync(DeadLetterQueueMode mode)
    {
        _host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.UseRabbitMq().AutoProvision().AutoPurgeOnStartup();

                opts.PublishAllMessages().ToRabbitQueue(_queueName);
                opts.ListenToRabbitQueue(_queueName)
                    .DeadLetterQueueing(new DeadLetterQueue(DeadLetterQueueName, mode));

                opts.LocalRoutingConventionDisabled = true;
            }).StartAsync();

        return _host.Services.GetRequiredService<IWolverineRuntime>()
            .Options.Transports.GetOrCreate<RabbitMqTransport>();
    }

    private static async Task<uint> queueDepthAsync(string queueName)
    {
        await using var conn = await new ConnectionFactory { HostName = "localhost" }.CreateConnectionAsync();
        await using var channel = await conn.CreateChannelAsync();
        var result = await channel.QueueDeclarePassiveAsync(queueName);
        return result.MessageCount;
    }

    private static async Task waitForDeadLetteredAsync(RabbitMqQueue deadLetterQueue)
    {
        var deadline = DateTimeOffset.UtcNow.Add(30.Seconds());
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (await deadLetterQueue.QueuedCountAsync() > 0) return;
            await Task.Delay(250.Milliseconds());
        }

        throw new TimeoutException("Never got a message into the dead letter queue");
    }

    /// <summary>
    /// <see cref="RabbitMqInteropFriendlyCallback.MoveToErrorsAsync"/> is the path that was never settling.
    /// It posts an enriched copy to the dead letter queue and used to stop there.
    /// </summary>
    [Fact]
    public async Task interop_friendly_dead_lettering_leaves_no_unacked_delivery_behind()
    {
        var transport = await bootstrapAsync(DeadLetterQueueMode.InteropFriendly);

        await _host.TrackActivity().DoNotAssertOnExceptionsDetected()
            .PublishMessageAndWaitAsync(new AlwaysErrors());

        await waitForDeadLetteredAsync(transport.Queues[DeadLetterQueueName]);

        // Closing the channel is the whole point: the broker requeues anything still unacked on it. Before
        // the fix, and with per-message acks, this came back as 1.
        await _host.StopAsync(TestContext.Current.CancellationToken);

        (await queueDepthAsync(_queueName)).ShouldBe(0u);

        // ...and exactly one copy was dead lettered, not two. The interop callback sends its own enriched
        // copy, so settling with a nack instead of an ack would have added a second via the DLX under
        // UseEnhancedDeadLettering.
        (await queueDepthAsync(DeadLetterQueueName)).ShouldBe(1u);
    }

    /// <summary>
    /// Native mode settles through <c>RabbitMqChannelCallback.moveToErrorQueueAsync</c>, which always nacked
    /// correctly. Here as the control: the behaviour that was already right has to stay right, including
    /// that the broker's own dead-letter-exchange still routes exactly one copy.
    /// </summary>
    [Fact]
    public async Task native_dead_lettering_still_leaves_no_unacked_delivery_behind()
    {
        var transport = await bootstrapAsync(DeadLetterQueueMode.Native);

        await _host.TrackActivity().DoNotAssertOnExceptionsDetected()
            .PublishMessageAndWaitAsync(new AlwaysErrors());

        await waitForDeadLetteredAsync(transport.Queues[DeadLetterQueueName]);

        await _host.StopAsync(TestContext.Current.CancellationToken);

        (await queueDepthAsync(_queueName)).ShouldBe(0u);
        (await queueDepthAsync(DeadLetterQueueName)).ShouldBe(1u);
    }
}
