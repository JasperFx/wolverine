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

public class version_source_override : IAsyncLifetime
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
                        m.DatabaseSchemaName = "version_src";
                        m.Projections.Snapshot<VersionSourceAggregate>(SnapshotLifecycle.Inline);
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
        var action = session.Events.StartStream<VersionSourceAggregate>(new VersionSourceStarted());
        await session.SaveChangesAsync();

        theStreamId = action.Id;
    }

    private async Task<VersionSourceAggregate> LoadAggregate()
    {
        await using var session = theStore.LightweightSession();
        var aggregate = await session.LoadAsync<VersionSourceAggregate>(theStreamId);
        aggregate.ShouldNotBeNull();
        return aggregate;
    }

    [Fact]
    public async Task happy_path_with_custom_version_source_on_aggregate_handler()
    {
        await GivenAggregate();

        await theHost.InvokeMessageAndWaitAsync(
            new IncrementWithCustomVersion(theStreamId, ExpectedVersion: 1));

        var aggregate = await LoadAggregate();
        aggregate.Count.ShouldBe(1);
    }

    [Fact]
    public async Task wrong_version_with_custom_version_source_on_aggregate_handler()
    {
        await GivenAggregate();

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
public record IncrementWithCustomVersion(Guid VersionSourceAggregateId, long ExpectedVersion);

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
