using IntegrationTests;
using JasperFx;
using JasperFx.Core;
using JasperFx.Events;
using JasperFx.Events.Projections;
using JasperFx.Resources;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Polecat;
using Polecat.Projections;
using Shouldly;
using Wolverine;
using Wolverine.Polecat;
using Wolverine.Runtime;
using Wolverine.Tracking;

namespace PolecatTests;

/// <summary>
///     The Polecat half of GH-4556. An <see cref="ISendMyself" /> published from RaiseSideEffects has
///     to be applied like a cascaded message rather than routed as the wrapper type. The original fix
///     landed in the Marten bridge only, so this store kept dropping them as NoRoutes -- all three
///     bridges now share <c>ProjectionSideEffectSink</c> precisely so that cannot happen again.
/// </summary>
public class Bug_4556_projection_side_effects_honor_send_myself : IAsyncLifetime
{
    private IHost theHost = null!;

    public async ValueTask InitializeAsync()
    {
        theHost = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Durability.MessageStorageSchemaName = "wolverine";
                opts.Durability.Mode = DurabilityMode.Solo;

                opts.Services.AddPolecat(m =>
                    {
                        m.ConnectionString = Servers.SqlServerConnectionString;
                        m.DatabaseSchemaName = "pc_send_myself";
                        m.Events.EnableSideEffectsOnInlineProjections = true;
                        m.Projections.Add(new PcSendMyselfProjection(), ProjectionLifecycle.Inline);
                    })
                    .UseLightweightSessions()
                    .IntegrateWithWolverine();

                opts.LocalQueue("pc-side-effects").UseDurableInbox();

                opts.Discovery.DisableConventionalDiscovery()
                    .IncludeType(typeof(PcSendMyselfHandler));

                opts.Services.AddResourceSetupOnStartup(StartupAction.ResetState);
            }).StartAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await theHost.StopAsync();
        theHost.Dispose();
    }

    [Fact]
    public async Task send_myself_with_metadata_is_applied_and_keeps_the_metadata()
    {
        var tracked = await publishAsync(withMetadata: true);
        var envelope = tracked.Received.SingleEnvelope<PcSendMyselfCommand>();

        envelope.Destination.ShouldBe(new Uri("local://pc-side-effects"));
        envelope.CorrelationId.ShouldBe(PcSendMyselfProjection.CorrelationId);
        envelope.Headers[EnvelopeConstants.CausationIdKey].ShouldBe(PcSendMyselfProjection.CausationId);
        envelope.Headers["custom"].ShouldBe("value");
    }

    [Fact]
    public async Task send_myself_without_metadata_is_applied_and_keeps_the_batch_correlation_id()
    {
        var tracked = await publishAsync(withMetadata: false);

        var sendMyself = tracked.Received.SingleEnvelope<PcSendMyselfCommand>();
        var plain = tracked.Received.SingleEnvelope<PcPlainCommand>();

        sendMyself.Destination.ShouldBe(new Uri("local://pc-side-effects"));
        plain.CorrelationId.ShouldNotBeNull();
        sendMyself.CorrelationId.ShouldBe(plain.CorrelationId);
    }

    private async Task<ITrackedSession> publishAsync(bool withMetadata)
    {
        return await theHost
            .TrackActivity()
            .Timeout(30.Seconds())
            .WaitForMessageToBeReceivedAt<PcSendMyselfCommand>(theHost)
            .WaitForMessageToBeReceivedAt<PcPlainCommand>(theHost)
            .ExecuteAndWaitAsync((Func<IMessageContext, Task>)(async _ =>
            {
                var store = theHost.Services.GetRequiredService<IDocumentStore>();
                await using var session = store.LightweightSession();
                session.Events.StartStream<PcSendMyselfAggregate>(Guid.NewGuid(),
                    new PcSendMyselfTriggered(withMetadata));
                await session.SaveChangesAsync();
            }));
    }
}

public record PcSendMyselfTriggered(bool WithMetadata);

public record PcSendMyselfCommand(Guid AggregateId);

public record PcPlainCommand(Guid AggregateId);

public class PcSendMyselfAggregate
{
    public Guid Id { get; set; }
    public bool WithMetadata { get; set; }
}

public class PcSendMyselfProjection : SingleStreamProjection<PcSendMyselfAggregate, Guid>
{
    public const string CorrelationId = "pc-side-effect-correlation";
    public const string CausationId = "pc-side-effect-causation";

    public static PcSendMyselfAggregate Create(PcSendMyselfTriggered e, IEvent metadata) =>
        new() { Id = metadata.StreamId, WithMetadata = e.WithMetadata };

    public override ValueTask RaiseSideEffects(IDocumentSession session, IEventSlice<PcSendMyselfAggregate> slice)
    {
        if (slice.Snapshot is null) return ValueTask.CompletedTask;

        slice.PublishMessage(new PcPlainCommand(slice.Snapshot.Id));

        var message = new PcSendMyselfCommand(slice.Snapshot.Id).ToEndpoint("pc-side-effects");

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

public static class PcSendMyselfHandler
{
    public static void Handle(PcSendMyselfCommand command)
    {
    }

    public static void Handle(PcPlainCommand command)
    {
    }
}
