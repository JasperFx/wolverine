using IntegrationTests;
using JasperFx;
using JasperFx.CodeGeneration;
using JasperFx.Resources;
using Marten;
using Marten.Events;
using Marten.Events.Projections;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine;
using Wolverine.Marten;
using Wolverine.Tracking;

namespace MartenTests.AggregateHandlerWorkflow;

public class version_source_override : PostgresqlContext, IAsyncLifetime
{
    private IHost theHost;
    private IDocumentStore theStore;
    private Guid theStreamId;

    public async Task InitializeAsync()
    {
        theHost = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Services.AddMarten(m =>
                    {
                        m.Connection(Servers.PostgresConnectionString);
                        m.Projections.Snapshot<VersionSourceAggregate>(SnapshotLifecycle.Inline);
                        m.DisableNpgsqlLogging = true;
                    })
                    .UseLightweightSessions()
                    .IntegrateWithWolverine();

                opts.Services.AddResourceSetupOnStartup();
                opts.CodeGeneration.TypeLoadMode = TypeLoadMode.Auto;
            }).StartAsync();

        theStore = theHost.Services.GetRequiredService<IDocumentStore>();
    }

    public async Task DisposeAsync()
    {
        await theHost.StopAsync();
        theHost.Dispose();
    }

    private async Task GivenAggregate()
    {
        await using var session = theStore.LightweightSession();
        var action = session.Events.StartStream<VersionSourceAggregate>(new VersionSourceStarted());
        await session.SaveChangesAsync();

        theStreamId = action.Id;
    }

    private async Task<VersionSourceAggregate> LoadAggregate()
    {
        await using var session = theStore.LightweightSession();
        return await session.LoadAsync<VersionSourceAggregate>(theStreamId);
    }

    [Fact]
    public async Task happy_path_with_custom_version_source_on_aggregate_handler()
    {
        await GivenAggregate();

        // ExpectedVersion = 1 matches the stream version after the start event
        await theHost.InvokeMessageAndWaitAsync(
            new IncrementWithCustomVersion(theStreamId, ExpectedVersion: 1));

        var aggregate = await LoadAggregate();
        aggregate.Count.ShouldBe(1);
    }

    [Fact]
    public async Task wrong_version_with_custom_version_source_on_aggregate_handler()
    {
        await GivenAggregate();

        // ExpectedVersion = 99 does not match the actual stream version of 1
        await Should.ThrowAsync<ConcurrencyException>(
            theHost.InvokeMessageAndWaitAsync(
                new IncrementWithCustomVersion(theStreamId, ExpectedVersion: 99)));
    }

    [Fact]
    public async Task happy_path_with_custom_version_source_on_write_aggregate()
    {
        await GivenAggregate();

        await theHost.InvokeMessageAndWaitAsync(
            new IncrementWithParamVersionSource(theStreamId, MyVersion: 1));

        var aggregate = await LoadAggregate();
        aggregate.Count.ShouldBe(1);
    }

    [Fact]
    public async Task wrong_version_with_custom_version_source_on_write_aggregate()
    {
        await GivenAggregate();

        await Should.ThrowAsync<ConcurrencyException>(
            theHost.InvokeMessageAndWaitAsync(
                new IncrementWithParamVersionSource(theStreamId, MyVersion: 99)));
    }
}

#region Types

public class VersionSourceAggregate
{
    public VersionSourceAggregate()
    {
    }

    public VersionSourceAggregate(VersionSourceStarted started)
    {
    }

    public Guid Id { get; set; }
    public int Count { get; set; }

    public void Apply(VersionSourceIncremented e) => Count++;
}

public record VersionSourceStarted;
public record VersionSourceIncremented;

#endregion

#region Commands

// Command with a non-standard version property name for AggregateHandler usage
public record IncrementWithCustomVersion(Guid VersionSourceAggregateId, long ExpectedVersion);

// Command with a non-standard version property name for WriteAggregate parameter usage
public record IncrementWithParamVersionSource(Guid VersionSourceAggregateId, long MyVersion);

#endregion

#region Handlers

[AggregateHandler(VersionSource = nameof(IncrementWithCustomVersion.ExpectedVersion))]
public static class CustomVersionSourceHandler
{
    public static VersionSourceIncremented Handle(IncrementWithCustomVersion command,
        VersionSourceAggregate aggregate)
    {
        return new VersionSourceIncremented();
    }
}

public static class ParamVersionSourceHandler
{
    public static VersionSourceIncremented Handle(IncrementWithParamVersionSource command,
        [WriteAggregate(VersionSource = nameof(IncrementWithParamVersionSource.MyVersion))]
        VersionSourceAggregate aggregate)
    {
        return new VersionSourceIncremented();
    }
}

#endregion
