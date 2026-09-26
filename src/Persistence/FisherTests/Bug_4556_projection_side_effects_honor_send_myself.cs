using Fisher;
using Fisher.Projections;
using JasperFx;
using JasperFx.Core;
using JasperFx.Events;
using JasperFx.Events.Projections;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine;
using Wolverine.Fisher;
using Wolverine.Runtime;
using Wolverine.Tracking;

namespace FisherTests;

/// <summary>
///     The Fisher half of GH-4556. An <see cref="ISendMyself" /> published from RaiseSideEffects has
///     to be applied like a cascaded message rather than routed as the wrapper type. The original fix
///     landed in the Marten bridge only, so this store kept dropping them as NoRoutes -- all three
///     bridges now share <c>ProjectionSideEffectSink</c> precisely so that cannot happen again.
/// </summary>
public class Bug_4556_projection_side_effects_honor_send_myself : IAsyncLifetime
{
    private FisherTestDatabase theDatabase = null!;
    private IHost theHost = null!;

    public async ValueTask InitializeAsync()
    {
        theDatabase = Servers.CreateDatabase("send_myself_side_effects");

        theHost = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Durability.Mode = DurabilityMode.Solo;

                opts.Services.AddFisher(m =>
                    {
                        m.Connection(theDatabase.ConnectionString);
                        m.AutoCreateSchemaObjects = AutoCreate.All;
                        m.Events.EnableSideEffectsOnInlineProjections = true;
                        m.Projections.Add(new FiSendMyselfProjection(), ProjectionLifecycle.Inline);
                    })
                    .ApplyAllDatabaseChangesOnStartup()
                    .IntegrateWithWolverine();

                opts.LocalQueue("fi-side-effects").UseDurableInbox();

                opts.Discovery.DisableConventionalDiscovery()
                    .IncludeType(typeof(FiSendMyselfHandler));
            }).StartAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await theHost.StopAsync();
        theHost.Dispose();
        theDatabase.Dispose();
    }

    [Fact]
    public async Task send_myself_with_metadata_is_applied_and_keeps_the_metadata()
    {
        var tracked = await publishAsync(withMetadata: true);
        var envelope = tracked.Received.SingleEnvelope<FiSendMyselfCommand>();

        envelope.Destination.ShouldBe(new Uri("local://fi-side-effects"));
        envelope.CorrelationId.ShouldBe(FiSendMyselfProjection.CorrelationId);
        envelope.Headers[EnvelopeConstants.CausationIdKey].ShouldBe(FiSendMyselfProjection.CausationId);
        envelope.Headers["custom"].ShouldBe("value");
    }

    [Fact]
    public async Task send_myself_without_metadata_is_applied_and_keeps_the_batch_correlation_id()
    {
        var tracked = await publishAsync(withMetadata: false);

        var sendMyself = tracked.Received.SingleEnvelope<FiSendMyselfCommand>();
        var plain = tracked.Received.SingleEnvelope<FiPlainCommand>();

        sendMyself.Destination.ShouldBe(new Uri("local://fi-side-effects"));
        plain.CorrelationId.ShouldNotBeNull();
        sendMyself.CorrelationId.ShouldBe(plain.CorrelationId);
    }

    private async Task<ITrackedSession> publishAsync(bool withMetadata)
    {
        return await theHost
            .TrackActivity()
            .Timeout(30.Seconds())
            .WaitForMessageToBeReceivedAt<FiSendMyselfCommand>(theHost)
            .WaitForMessageToBeReceivedAt<FiPlainCommand>(theHost)
            .ExecuteAndWaitAsync((Func<IMessageContext, Task>)(async _ =>
            {
                var store = theHost.Services.GetRequiredService<IDocumentStore>();
                await using var session = store.LightweightSession();
                session.Events.StartStream<FiSendMyselfAggregate>(Guid.NewGuid(),
                    new FiSendMyselfTriggered(withMetadata));
                await session.SaveChangesAsync();
            }));
    }
}

public record FiSendMyselfTriggered(bool WithMetadata);

public record FiSendMyselfCommand(Guid AggregateId);

public record FiPlainCommand(Guid AggregateId);

public class FiSendMyselfAggregate
{
    public Guid Id { get; set; }
    public bool WithMetadata { get; set; }
}

public class FiSendMyselfProjection : SingleStreamProjection<FiSendMyselfAggregate, Guid>
{
    public const string CorrelationId = "fi-side-effect-correlation";
    public const string CausationId = "fi-side-effect-causation";

    public static FiSendMyselfAggregate Create(FiSendMyselfTriggered e, IEvent metadata) =>
        new() { Id = metadata.StreamId, WithMetadata = e.WithMetadata };

    public override ValueTask RaiseSideEffects(IDocumentSession session, IEventSlice<FiSendMyselfAggregate> slice)
    {
        if (slice.Snapshot is null) return ValueTask.CompletedTask;

        slice.PublishMessage(new FiPlainCommand(slice.Snapshot.Id));

        var message = new FiSendMyselfCommand(slice.Snapshot.Id).ToEndpoint("fi-side-effects");

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

public static class FiSendMyselfHandler
{
    public static void Handle(FiSendMyselfCommand command)
    {
    }

    public static void Handle(FiPlainCommand command)
    {
    }
}
