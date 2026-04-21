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
using Wolverine.Tracking;

namespace MartenTests.Bugs;

/// <summary>
///     GH-2545: when a Marten projection publishes a side-effect message via
///     <see cref="IEventSlice{T}.PublishMessage(object, MessageMetadata)"/>, the
///     resulting Wolverine envelope — and the Marten <c>IDocumentSession</c> that
///     the handler opens for that envelope — must inherit the user-supplied
///     <see cref="MessageMetadata.CorrelationId"/> and
///     <see cref="MessageMetadata.CausationId"/>. This is the "todo-list" pattern
///     the issue describes: Event A opens a task keyed by a correlation id,
///     Event B (emitted by the handler of a side-effect command) closes the
///     task with the same correlation id.
/// </summary>
public class Bug_2545_raise_side_effects_with_metadata_override
{
    [Fact]
    public async Task metadata_on_PublishMessage_flows_to_handler_context_and_marten_session()
    {
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Durability.Mode = DurabilityMode.Solo;

                opts.Services.AddMarten(m =>
                {
                    m.Connection(Servers.PostgresConnectionString);
                    m.DatabaseSchemaName = "bug_2545";
                    m.Projections.Add<Bug2545Projection>(ProjectionLifecycle.Async);
                    m.DisableNpgsqlLogging = true;
                })
                .IntegrateWithWolverine()
                .AddAsyncDaemon(DaemonMode.Solo);

                opts.Policies.UseDurableLocalQueues();

                opts.Discovery.DisableConventionalDiscovery()
                    .IncludeType(typeof(Bug2545SideEffectHandler));
            }).StartAsync();

        var streamId = Guid.NewGuid();
        Bug2545SideEffectHandler.Received.Clear();

        // Clear any prior state from earlier test runs.
        var store = host.Services.GetRequiredService<IDocumentStore>();
        await store.Advanced.Clean.CompletelyRemoveAllAsync();

        var tracked = await host
            .TrackActivity()
            .Timeout(30.Seconds())
            .WaitForMessageToBeReceivedAt<Bug2545Command>(host)
            .ExecuteAndWaitAsync((Func<IMessageContext, Task>)(async _ =>
            {
                await using var session = store.LightweightSession();
                session.Events.StartStream<Bug2545Aggregate>(streamId, new Bug2545Triggered());
                await session.SaveChangesAsync();
            }));

        tracked.Executed.SingleMessage<Bug2545Command>().ShouldNotBeNull();

        // 1) The handler observed the user-supplied correlation id.
        Bug2545SideEffectHandler.ObservedCorrelationId.ShouldBe(Bug2545Projection.CorrelationId);

        // 2) The handler's MessageContext rolled the causation override from
        //    the envelope header onto the IDocumentSession, so any events the
        //    handler had appended would carry CausationId = the user override
        //    rather than Wolverine's default Guid-typed ConversationId string.
        Bug2545SideEffectHandler.ObservedSessionCausationId.ShouldBe(Bug2545Projection.CausationId);

        // 3) And CorrelationId on the session matches (parity with the default
        //    OutboxedSessionFactory behavior on the non-override path).
        Bug2545SideEffectHandler.ObservedSessionCorrelationId.ShouldBe(Bug2545Projection.CorrelationId);
    }
}

public record Bug2545Triggered;

public record Bug2545Command(Guid AggregateId);

public class Bug2545Aggregate
{
    public Guid Id { get; set; }

    public static Bug2545Aggregate Create(Bug2545Triggered _) => new();
}

public class Bug2545Projection : SingleStreamProjection<Bug2545Aggregate, Guid>
{
    public const string CorrelationId = "todo-correlation-42";
    public const string CausationId = "caused-by-triggered-event";

    public static Bug2545Aggregate Create(Bug2545Triggered _) => new();

    public override ValueTask RaiseSideEffects(
        Marten.IDocumentOperations operations,
        IEventSlice<Bug2545Aggregate> slice)
    {
        if (slice.Snapshot is null) return ValueTask.CompletedTask;

        slice.PublishMessage(
            new Bug2545Command(slice.Snapshot.Id),
            new MessageMetadata(slice.TenantId)
            {
                CorrelationId = CorrelationId,
                CausationId = CausationId
            });

        return ValueTask.CompletedTask;
    }
}

public static class Bug2545SideEffectHandler
{
    public static readonly List<Bug2545Command> Received = new();
    public static string? ObservedCorrelationId;
    public static string? ObservedSessionCausationId;
    public static string? ObservedSessionCorrelationId;

    public static void Handle(Bug2545Command cmd, IMessageContext context, IDocumentSession session)
    {
        Received.Add(cmd);
        ObservedCorrelationId = context.CorrelationId;
        ObservedSessionCausationId = session.CausationId;
        ObservedSessionCorrelationId = session.CorrelationId;
    }
}
