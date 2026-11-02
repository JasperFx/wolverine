using System.Collections.Concurrent;
using System.Diagnostics;
using IntegrationTests;
using JasperFx;
using JasperFx.OpenTelemetry;
using JasperFx.Resources;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine;
using Wolverine.Marten;
using Wolverine.Runtime;
using Wolverine.Tracking;
using Wolverine.Util;

namespace MartenTests;

/// <summary>
/// GH-5310. With Marten's <c>OpenTelemetry.TrackConnections</c> on, the <c>marten.connection</c> activity
/// becomes <see cref="Activity.Current"/> as soon as the session is built and stays current for the rest of
/// the handler. The generated handler, saga and aggregate tagging used to read <see cref="Activity.Current"/>,
/// so every one of those tags landed on <c>marten.connection</c> instead of the message's own span.
/// </summary>
public class span_tags_stay_on_the_message_span_5310 : PostgresqlContext, IAsyncLifetime
{
    private readonly ConcurrentQueue<Activity> _stopped = new();
    private ActivityListener _listener = null!;
    private IHost _host = null!;

    public async ValueTask InitializeAsync()
    {
        _listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name is "Wolverine" or "Marten",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity => _stopped.Enqueue(activity)
        };
        ActivitySource.AddActivityListener(_listener);

        _host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Discovery.DisableConventionalDiscovery()
                    .IncludeType(typeof(SpanTagged5310Handler))
                    .IncludeType(typeof(SpanTagged5310Saga));

                opts.Durability.Mode = DurabilityMode.Solo;

                opts.Services.AddMarten(m =>
                    {
                        m.Connection(Servers.PostgresConnectionString);
                        m.DatabaseSchemaName = "span_tags_5310";
                        m.OpenTelemetry.TrackConnections = TrackLevel.Verbose;
                    })
                    .IntegrateWithWolverine();

                opts.Policies.AutoApplyTransactions();
            }).StartAsync();

        await _host.ResetResourceState();
    }

    public async ValueTask DisposeAsync()
    {
        _listener.Dispose();
        await _host.StopAsync();
        _host.Dispose();
    }

    // The span the executor started for the message: named for the message type, from the Wolverine source.
    // Each test sends each of its message types exactly once.
    private Activity messageSpanFor<T>()
    {
        var messageType = typeof(T).ToMessageTypeName();
        return _stopped
            .Where(x => x.Source.Name == "Wolverine" && x.OperationName == messageType)
            .ShouldHaveSingleItem();
    }

    private void theMartenConnectionSpansCarryNoWolverineTags()
    {
        var connections = _stopped.Where(x => x.OperationName == "marten.connection").ToArray();

        // Proves the scenario reproduces: without connection spans there is nothing for the tags to leak onto
        connections.ShouldNotBeEmpty();

        foreach (var connection in connections)
        {
            connection.GetTagItem(WolverineTracing.HandlerType).ShouldBeNull();
            connection.GetTagItem(WolverineTracing.MessageHandler).ShouldBeNull();
            connection.GetTagItem(WolverineTracing.StreamId).ShouldBeNull();
            connection.GetTagItem(WolverineTracing.StreamType).ShouldBeNull();
            connection.GetTagItem(WolverineTracing.SagaId).ShouldBeNull();
            connection.GetTagItem(WolverineTracing.SagaType).ShouldBeNull();
        }
    }

    [Fact]
    public async Task aggregate_handler_tags_land_on_the_message_span()
    {
        var streamId = Guid.NewGuid();
        await using (var session = _host.DocumentStore().LightweightSession())
        {
            session.Events.StartStream<SpanTagged5310Aggregate>(streamId, new SpanTagged5310Bumped());
            await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await _host.InvokeMessageAndWaitAsync(new BumpSpanTagged5310(streamId));

        var span = messageSpanFor<BumpSpanTagged5310>();

        span.GetTagItem(WolverineTracing.HandlerType).ShouldBe(typeof(SpanTagged5310Handler).FullName);
        span.GetTagItem(WolverineTracing.MessageHandler).ShouldBe(typeof(SpanTagged5310Handler).FullName);
        span.GetTagItem(WolverineTracing.StreamId).ShouldBe(streamId);
        span.GetTagItem(WolverineTracing.StreamType).ShouldBe(typeof(SpanTagged5310Aggregate).FullName);

        theMartenConnectionSpansCarryNoWolverineTags();
    }

    [Fact]
    public async Task saga_tags_land_on_the_message_span_when_the_saga_starts_and_continues()
    {
        var sagaId = Guid.NewGuid();

        await _host.InvokeMessageAndWaitAsync(new StartSpanTagged5310Saga(sagaId));
        await _host.InvokeMessageAndWaitAsync(new TouchSpanTagged5310Saga(sagaId));

        var start = messageSpanFor<StartSpanTagged5310Saga>();
        start.GetTagItem(WolverineTracing.HandlerType).ShouldBe(typeof(SpanTagged5310Saga).FullName);
        start.GetTagItem(WolverineTracing.MessageHandler).ShouldBe(typeof(SpanTagged5310Saga).FullName);
        start.GetTagItem(WolverineTracing.SagaId).ShouldBe(sagaId.ToString());
        start.GetTagItem(WolverineTracing.SagaType).ShouldBe(typeof(SpanTagged5310Saga).FullName);

        var touch = messageSpanFor<TouchSpanTagged5310Saga>();
        touch.GetTagItem(WolverineTracing.HandlerType).ShouldBe(typeof(SpanTagged5310Saga).FullName);
        touch.GetTagItem(WolverineTracing.SagaId).ShouldBe(sagaId.ToString());
        touch.GetTagItem(WolverineTracing.SagaType).ShouldBe(typeof(SpanTagged5310Saga).FullName);

        theMartenConnectionSpansCarryNoWolverineTags();
    }
}

public class SpanTagged5310Aggregate
{
    public Guid Id { get; set; }
    public int Count { get; set; }

    public void Apply(SpanTagged5310Bumped _) => Count++;
}

public record SpanTagged5310Bumped;

public record BumpSpanTagged5310(Guid Id);

[AggregateHandler]
public static class SpanTagged5310Handler
{
    public static SpanTagged5310Bumped Handle(BumpSpanTagged5310 command, SpanTagged5310Aggregate aggregate) => new();
}

public record StartSpanTagged5310Saga(Guid Id);

public record TouchSpanTagged5310Saga(Guid SpanTagged5310SagaId);

public class SpanTagged5310Saga : Wolverine.Saga
{
    public Guid Id { get; set; }
    public int Touches { get; set; }

    public static SpanTagged5310Saga Start(StartSpanTagged5310Saga command) => new() { Id = command.Id };

    public void Handle(TouchSpanTagged5310Saga command) => Touches++;
}
