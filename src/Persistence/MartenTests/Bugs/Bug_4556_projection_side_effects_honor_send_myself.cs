using IntegrationTests;
using JasperFx;
using JasperFx.Core;
using JasperFx.Events;
using JasperFx.Events.Daemon;
using JasperFx.Events.Projections;
using Marten;
using Marten.Events.Aggregation;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Npgsql;
using Shouldly;
using Weasel.Postgresql;
using Wolverine;
using Wolverine.Marten;
using Wolverine.Runtime;
using Wolverine.Tracking;

namespace MartenTests.Bugs;

/// <summary>
///     GH-4556: a projection side effect that publishes an <see cref="ISendMyself" /> (ToEndpoint(),
///     DelayedFor(), SignalR's ToWebSocketGroup(), ...) must be applied like a cascaded message, not
///     routed as the wrapper type -- which has no subscriber, so the message was silently dropped.
/// </summary>
/// <remarks>
///     Every scenario publishes BOTH the ISendMyself and a plain message out of the same slice, so
///     the two paths are compared inside a single run. That control is what catches the ISendMyself
///     path drifting away from the ordinary one -- e.g. losing its correlation id.
/// </remarks>
public class Bug_4556_projection_side_effects_honor_send_myself
{
    [Fact]
    public async Task send_myself_published_with_metadata_is_applied_and_keeps_the_metadata()
    {
        using var host = await startHostAsync("send_myself_metadata", ProjectionLifecycle.Async);

        var tracked = await publishFromProjectionAsync(host, withMetadata: true);
        var envelope = tracked.Received.SingleEnvelope<SendMyselfCommand>();

        envelope.Destination.ShouldBe(new Uri("local://side-effects"));
        envelope.CorrelationId.ShouldBe(SendMyselfProjection.CorrelationId);
        envelope.Headers[EnvelopeConstants.CausationIdKey].ShouldBe(SendMyselfProjection.CausationId);
        envelope.Headers["custom"].ShouldBe("value");
    }

    [Fact]
    public async Task send_myself_published_without_metadata_is_applied()
    {
        using var host = await startHostAsync("send_myself_plain", ProjectionLifecycle.Async);

        var tracked = await publishFromProjectionAsync(host, withMetadata: false);

        tracked.Received.SingleEnvelope<SendMyselfCommand>()
            .Destination.ShouldBe(new Uri("local://side-effects"));
    }

    // The regression this test class exists to prevent a second time. MessageMetadata.CorrelationId
    // is plain null unless the projection set one, so taking it over unconditionally stamped null on
    // the envelope -- while the plain message out of the very same slice kept the batch's id.
    [Fact]
    public async Task send_myself_without_metadata_keeps_the_same_correlation_id_as_a_plain_message()
    {
        using var host = await startHostAsync("send_myself_correlation", ProjectionLifecycle.Async);

        var tracked = await publishFromProjectionAsync(host, withMetadata: false);

        var sendMyself = tracked.Received.SingleEnvelope<SendMyselfCommand>();
        var plain = tracked.Received.SingleEnvelope<PlainSideEffectCommand>();

        plain.CorrelationId.ShouldNotBeNull();
        sendMyself.CorrelationId.ShouldBe(plain.CorrelationId);
    }

    // The inline path takes a different route to the same batch: Marten caches it on the session
    // (DocumentSessionBase.StartMessageBatch) instead of on the daemon's ProjectionUpdateBatch.
    [Fact]
    public async Task send_myself_is_applied_from_an_inline_projection()
    {
        using var host = await startHostAsync("send_myself_inline", ProjectionLifecycle.Inline);

        var tracked = await publishFromProjectionAsync(host, withMetadata: false);

        tracked.Received.SingleEnvelope<SendMyselfCommand>()
            .Destination.ShouldBe(new Uri("local://side-effects"));
    }

    private static async Task<IHost> startHostAsync(string schemaName, ProjectionLifecycle lifecycle)
    {
        // Drop BEFORE the host starts. Cleaning after StartAsync() pulls mt_event_progression out
        // from under a daemon that is already polling it.
        await using var conn = new NpgsqlConnection(Servers.PostgresConnectionString);
        await conn.OpenAsync(TestContext.Current.CancellationToken);
        await conn.DropSchemaAsync(schemaName);
        await conn.CloseAsync();

        return await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Durability.Mode = DurabilityMode.Solo;

                opts.Services.AddMarten(m =>
                    {
                        m.Connection(Servers.PostgresConnectionString);
                        m.DatabaseSchemaName = schemaName;
                        m.Projections.Add<SendMyselfProjection>(lifecycle);
                        m.Events.EnableSideEffectsOnInlineProjections = true;
                        m.DisableNpgsqlLogging = true;
                    })
                    .IntegrateWithWolverine()
                    .AddAsyncDaemon(DaemonMode.Solo);

                opts.LocalQueue("side-effects").UseDurableInbox();

                opts.Discovery.DisableConventionalDiscovery()
                    .IncludeType(typeof(SendMyselfSideEffectHandler));
            }).StartAsync(TestContext.Current.CancellationToken);
    }

    private static async Task<ITrackedSession> publishFromProjectionAsync(IHost host, bool withMetadata)
    {
        var store = host.Services.GetRequiredService<IDocumentStore>();

        return await host
            .TrackActivity()
            .Timeout(30.Seconds())
            .WaitForMessageToBeReceivedAt<SendMyselfCommand>(host)
            .WaitForMessageToBeReceivedAt<PlainSideEffectCommand>(host)
            .ExecuteAndWaitAsync((Func<IMessageContext, Task>)(async _ =>
            {
                await using var session = store.LightweightSession();
                session.Events.StartStream<SendMyselfAggregate>(Guid.NewGuid(),
                    new SendMyselfTriggered(withMetadata));
                await session.SaveChangesAsync();
            }));
    }
}

public record SendMyselfTriggered(bool WithMetadata);

public record SendMyselfCommand(Guid AggregateId);

public record PlainSideEffectCommand(Guid AggregateId);

public class SendMyselfAggregate
{
    public Guid Id { get; set; }
    public bool WithMetadata { get; set; }
}

public class SendMyselfProjection : SingleStreamProjection<SendMyselfAggregate, Guid>
{
    public const string CorrelationId = "side-effect-correlation";
    public const string CausationId = "side-effect-causation";

    public static SendMyselfAggregate Create(SendMyselfTriggered e) => new() { WithMetadata = e.WithMetadata };

    public override ValueTask RaiseSideEffects(IDocumentOperations operations, IEventSlice<SendMyselfAggregate> slice)
    {
        if (slice.Snapshot is null) return ValueTask.CompletedTask;

        // The control: an ordinary message, same slice, same metadata treatment.
        slice.PublishMessage(new PlainSideEffectCommand(slice.Snapshot.Id));

        var message = new SendMyselfCommand(slice.Snapshot.Id).ToEndpoint("side-effects");

        if (slice.Snapshot.WithMetadata)
        {
            slice.PublishMessage(message,
                new MessageMetadata(slice.TenantId) { CorrelationId = CorrelationId, CausationId = CausationId }
                    .WithHeader("custom", "value"));
        }
        else
        {
            slice.PublishMessage(message);
        }

        return ValueTask.CompletedTask;
    }
}

public static class SendMyselfSideEffectHandler
{
    public static void Handle(SendMyselfCommand command)
    {
    }

    public static void Handle(PlainSideEffectCommand command)
    {
    }
}
