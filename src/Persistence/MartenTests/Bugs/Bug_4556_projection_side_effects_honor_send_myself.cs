using IntegrationTests;
using JasperFx;
using JasperFx.Core;
using JasperFx.Events;
using JasperFx.Events.Daemon;
using JasperFx.Events.Grouping;
using JasperFx.Events.Projections;
using Marten;
using Marten.Events.Aggregation;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine;
using Wolverine.Marten;
using Wolverine.Runtime;
using Wolverine.Tracking;

namespace MartenTests.Bugs;

/// <summary>
///     GH-4556: a projection side effect that publishes an <see cref="ISendMyself"/> (ToEndpoint(), DelayedFor(),
///     SignalR's ToWebSocketGroup(), ...) must be applied like a cascaded message, not routed as the wrapper
///     type — which has no subscriber, so the message was silently dropped.
/// </summary>
public class Bug_4556_projection_side_effects_honor_send_myself
{
    [Fact]
    public async Task send_myself_published_with_metadata_is_applied_and_keeps_the_metadata()
    {
        using var host = await startHostAsync("send_myself_metadata");

        var envelope = await publishFromProjectionAsync(host, new SendMyselfTriggered(WithMetadata: true));

        envelope.Destination.ShouldBe(new Uri("local://side-effects"));
        envelope.CorrelationId.ShouldBe(SendMyselfProjection.CorrelationId);
        envelope.Headers[EnvelopeConstants.CausationIdKey].ShouldBe(SendMyselfProjection.CausationId);
        envelope.Headers["custom"].ShouldBe("value");
    }

    [Fact]
    public async Task send_myself_published_without_metadata_is_applied()
    {
        using var host = await startHostAsync("send_myself_plain");

        var envelope = await publishFromProjectionAsync(host, new SendMyselfTriggered(WithMetadata: false));

        envelope.Destination.ShouldBe(new Uri("local://side-effects"));
    }

    private static async Task<IHost> startHostAsync(string schemaName)
    {
        var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Durability.Mode = DurabilityMode.Solo;

                opts.Services.AddMarten(m =>
                    {
                        m.Connection(Servers.PostgresConnectionString);
                        m.DatabaseSchemaName = schemaName;
                        m.Projections.Add<SendMyselfProjection>(ProjectionLifecycle.Async);
                        m.DisableNpgsqlLogging = true;
                    })
                    .IntegrateWithWolverine()
                    .AddAsyncDaemon(DaemonMode.Solo);

                opts.LocalQueue("side-effects").UseDurableInbox();

                opts.Discovery.DisableConventionalDiscovery()
                    .IncludeType(typeof(SendMyselfSideEffectHandler));
            }).StartAsync(TestContext.Current.CancellationToken);

        await host.Services.GetRequiredService<IDocumentStore>().Advanced.Clean
            .CompletelyRemoveAllAsync(TestContext.Current.CancellationToken);

        return host;
    }

    private static async Task<Envelope> publishFromProjectionAsync(IHost host, SendMyselfTriggered triggered)
    {
        var store = host.Services.GetRequiredService<IDocumentStore>();

        var tracked = await host
            .TrackActivity()
            .Timeout(30.Seconds())
            .WaitForMessageToBeReceivedAt<SendMyselfCommand>(host)
            .ExecuteAndWaitAsync((Func<IMessageContext, Task>)(async _ =>
            {
                await using var session = store.LightweightSession();
                session.Events.StartStream<SendMyselfAggregate>(Guid.NewGuid(), triggered);
                await session.SaveChangesAsync();
            }));

        return tracked.Received.SingleEnvelope<SendMyselfCommand>();
    }
}

public record SendMyselfTriggered(bool WithMetadata);

public record SendMyselfCommand(Guid AggregateId);

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
}
