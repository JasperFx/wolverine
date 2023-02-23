using System;
using System.Threading;
using System.Threading.Tasks;
using Marten;
using Marten.Events.Projections;
using Shouldly;
using TeleHealth.Common;
using Xunit;
using Xunit.Abstractions;

namespace TeleHealth.Tests;

public class GettingStarted
{
    private readonly ITestOutputHelper _output;

    public GettingStarted(ITestOutputHelper output)
    {
        _output = output;
    }

    public async Task rebuild_projection(
        IDocumentStore store,
        CancellationToken cancellation)
    {
        using var daemon = await store
            .BuildProjectionDaemonAsync();

        await daemon
            .RebuildProjection<AppointmentDurationProjection>(
                cancellation);
    }

    public async Task time_travel(
        IQuerySession session,
        Guid shiftId,
        DateTimeOffset atTime)
    {
        // Fetch all the events for the stream, and
        // apply them to a ProviderShift aggregate
        var shift = await session
            .Events
            .AggregateStreamAsync<ProviderShift>(
                shiftId,
                timestamp: atTime);
    }

    [Fact]
    public async Task append_events()
    {
        // This would be an input
        var boardId = Guid.NewGuid();

        var store = DocumentStore.For("connection string");
        await using var session = store.LightweightSession();

        var shiftId = session.Events.StartStream<ProviderShift>(
            new ProviderJoined(boardId, Guid.NewGuid()),
            new ProviderReady()
        ).Id;

        // The ProviderShift aggregate will be
        // updated at this time
        await session.SaveChangesAsync();

        // Load the persisted ProviderShift right out
        // of the database
        var shift = await session
            .LoadAsync<ProviderShift>(shiftId);
    }

    [Fact]
    public async Task start_a_new_shift()
    {
// Initialize Marten the simplest, possible way
        var store = DocumentStore.For(opts =>
        {
            opts.Connection(ConnectionSource.ConnectionString);
            opts.Projections.SelfAggregate<ProviderShift>(
                ProjectionLifecycle.Inline);
        });

        var provider = new Provider
        {
            FirstName = "Larry",
            LastName = "Bird"
        };

// Just a little reference data
        await using var session = store.LightweightSession();
        session.Store(provider);
        await session.SaveChangesAsync();

        var boardId = Guid.NewGuid();

// Just to capture the SQL being executed to the test output
        session.Logger = new TestOutputMartenLogger(_output);

        var shiftId = session.Events.StartStream<ProviderShift>
        (
            new ProviderJoined(provider.Id, boardId),
            new ProviderReady()
        ).Id;

        await session.SaveChangesAsync();


        var shift = await session.Events.AggregateStreamAsync<ProviderShift>(shiftId,
            timestamp: DateTime.Today.AddHours(13));

        shift.Name.ShouldBe("Larry Bird");
    }
}