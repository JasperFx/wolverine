using IntegrationTests;
using JasperFx.Events.Projections;
using JasperFx;
using JasperFx.CodeGeneration;
using JasperFx.Events;
using JasperFx.Resources;
using Polecat;
using Polecat.Events;
using Polecat.Projections;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine;
using Wolverine.Polecat;
using Wolverine.Tracking;

namespace PolecatTests.AggregateHandlerWorkflow;

public class always_enforce_consistency_workflow : IAsyncLifetime
{
    private IHost theHost = null!;
    private IDocumentStore theStore = null!;
    private Guid theStreamId;

    public async Task InitializeAsync()
    {
        theHost = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Services.AddPolecat(m =>
                    {
                        m.ConnectionString = Servers.SqlServerConnectionString;
                        m.DatabaseSchemaName = "consistency";
                        m.Projections.Snapshot<ConsistencyAggregate>(SnapshotLifecycle.Inline);
                    })
                    .UseLightweightSessions()
                    .IntegrateWithWolverine();

                opts.Services.AddResourceSetupOnStartup();
                opts.CodeGeneration.TypeLoadMode = TypeLoadMode.Auto;
            }).StartAsync();

        theStore = theHost.Services.GetRequiredService<IDocumentStore>();
        await ((DocumentStore)theStore).Database.ApplyAllConfiguredChangesToDatabaseAsync();
    }

    public async Task DisposeAsync()
    {
        await theHost.StopAsync();
        theHost.Dispose();
    }

    private async Task GivenAggregate()
    {
        await using var session = theStore.LightweightSession();
        var action = session.Events.StartStream<ConsistencyAggregate>(new ConsistencyStarted());
        await session.SaveChangesAsync();

        theStreamId = action.Id;
    }

    private async Task<ConsistencyAggregate> LoadAggregate()
    {
        await using var session = theStore.LightweightSession();
        return await session.LoadAsync<ConsistencyAggregate>(theStreamId);
    }

    [Fact]
    public async Task happy_path_with_events_using_attribute_property()
    {
        await GivenAggregate();
        await theHost.InvokeMessageAndWaitAsync(new ConsistentIncrementA(theStreamId));

        var aggregate = await LoadAggregate();
        aggregate.ACount.ShouldBe(1);
    }

    [Fact]
    public async Task happy_path_no_events_emitted_using_attribute_property()
    {
        await GivenAggregate();

        await theHost.InvokeMessageAndWaitAsync(new ConsistentDoNothing(theStreamId));

        var aggregate = await LoadAggregate();
        aggregate.ACount.ShouldBe(0);
    }

    [Fact]
    public async Task concurrency_violation_no_events_emitted_using_attribute_property()
    {
        await GivenAggregate();

        await Should.ThrowAsync<ConcurrencyException>(
            theHost.InvokeMessageAndWaitAsync(
                new ConsistentDoNothingWithConcurrentModification(theStreamId)));
    }

    [Fact]
    public async Task happy_path_with_events_using_consistent_aggregate_handler_attribute()
    {
        await GivenAggregate();
        await theHost.InvokeMessageAndWaitAsync(new ConsistentHandlerIncrementA(theStreamId));

        var aggregate = await LoadAggregate();
        aggregate.ACount.ShouldBe(1);
    }

    [Fact]
    public async Task happy_path_no_events_using_consistent_aggregate_handler_attribute()
    {
        await GivenAggregate();

        await theHost.InvokeMessageAndWaitAsync(new ConsistentHandlerDoNothing(theStreamId));

        var aggregate = await LoadAggregate();
        aggregate.ACount.ShouldBe(0);
    }

    [Fact]
    public async Task concurrency_violation_no_events_using_consistent_aggregate_handler_attribute()
    {
        await GivenAggregate();

        await Should.ThrowAsync<ConcurrencyException>(
            theHost.InvokeMessageAndWaitAsync(
                new ConsistentHandlerDoNothingWithConcurrentModification(theStreamId)));
    }

    [Fact]
    public async Task happy_path_using_parameter_level_consistent_aggregate_attribute()
    {
        await GivenAggregate();
        await theHost.InvokeMessageAndWaitAsync(new ConsistentParamIncrementA(theStreamId));

        var aggregate = await LoadAggregate();
        aggregate.ACount.ShouldBe(1);
    }

    [Fact]
    public async Task concurrency_violation_using_parameter_level_consistent_aggregate_attribute()
    {
        await GivenAggregate();

        await Should.ThrowAsync<ConcurrencyException>(
            theHost.InvokeMessageAndWaitAsync(
                new ConsistentParamDoNothingWithConcurrentModification(theStreamId)));
    }
}

#region Aggregate and Events

public class ConsistencyAggregate
{
    public ConsistencyAggregate()
    {
    }

    public ConsistencyAggregate(ConsistencyStarted started)
    {
    }

    public Guid Id { get; set; }
    public int ACount { get; set; }

    public void Apply(ConsistencyAEvent e) => ACount++;
}

public record ConsistencyStarted;
public record ConsistencyAEvent;

#endregion

#region Commands
public record ConsistentIncrementA(Guid ConsistencyAggregateId);
public record ConsistentDoNothing(Guid ConsistencyAggregateId);
public record ConsistentHandlerIncrementA(Guid ConsistencyAggregateId);
public record ConsistentHandlerDoNothing(Guid ConsistencyAggregateId);
public record ConsistentParamIncrementA(Guid ConsistencyAggregateId);

public record ConsistentDoNothingWithConcurrentModification(Guid ConsistencyAggregateId);
public record ConsistentHandlerDoNothingWithConcurrentModification(Guid ConsistencyAggregateId);
public record ConsistentParamDoNothingWithConcurrentModification(Guid ConsistencyAggregateId);

#endregion

#region Handlers using AggregateHandler with AlwaysEnforceConsistency property

[AggregateHandler(AlwaysEnforceConsistency = true)]
public static class ConsistentPropertyHandler
{
    public static ConsistencyAEvent Handle(ConsistentIncrementA command, ConsistencyAggregate aggregate)
    {
        return new ConsistencyAEvent();
    }

    public static void Handle(ConsistentDoNothing command, IEventStream<ConsistencyAggregate> stream)
    {
    }

    public static async Task Handle(ConsistentDoNothingWithConcurrentModification command,
        IEventStream<ConsistencyAggregate> stream, IDocumentStore store)
    {
        await using var sneakySession = store.LightweightSession();
        sneakySession.Events.Append(command.ConsistencyAggregateId, new ConsistencyAEvent());
        await sneakySession.SaveChangesAsync();
    }
}

#endregion

#region Handlers using ConsistentAggregateHandler attribute

[ConsistentAggregateHandler]
public static class ConsistentAggregateHandlerUsage
{
    public static ConsistencyAEvent Handle(ConsistentHandlerIncrementA command, ConsistencyAggregate aggregate)
    {
        return new ConsistencyAEvent();
    }

    public static void Handle(ConsistentHandlerDoNothing command, IEventStream<ConsistencyAggregate> stream)
    {
    }

    public static async Task Handle(ConsistentHandlerDoNothingWithConcurrentModification command,
        IEventStream<ConsistencyAggregate> stream, IDocumentStore store)
    {
        await using var sneakySession = store.LightweightSession();
        sneakySession.Events.Append(command.ConsistencyAggregateId, new ConsistencyAEvent());
        await sneakySession.SaveChangesAsync();
    }
}

#endregion

#region Handlers using parameter-level ConsistentAggregate attribute

public static class ConsistentParamHandler
{
    public static ConsistencyAEvent Handle(ConsistentParamIncrementA command,
        [ConsistentAggregate] ConsistencyAggregate aggregate)
    {
        return new ConsistencyAEvent();
    }

    public static async Task Handle(ConsistentParamDoNothingWithConcurrentModification command,
        [ConsistentAggregate] IEventStream<ConsistencyAggregate> stream, IDocumentStore store)
    {
        await using var sneakySession = store.LightweightSession();
        sneakySession.Events.Append(stream.Id, new ConsistencyAEvent());
        await sneakySession.SaveChangesAsync();
    }
}

#endregion
