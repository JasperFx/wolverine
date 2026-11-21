using IntegrationTests;
using JasperFx.Events;
using Marten;
using Shouldly;
using Wolverine.Marten;

namespace MartenTests.AggregateHandlerWorkflow;

public class pending_event_metadata_stamp
{
    [Theory]
    [InlineData(EventAppendMode.Rich)]
    [InlineData(EventAppendMode.Quick)]
    [InlineData(EventAppendMode.QuickWithServerTimestamps)]
    public async Task stamps_every_pending_stream_the_way_the_commit_would(EventAppendMode mode)
    {
        using var store = DocumentStore.For(opts =>
        {
            opts.Connection(Servers.PostgresConnectionString);
            opts.DatabaseSchemaName = "pending_event_metadata";
            opts.DisableNpgsqlLogging = true;
            opts.Events.AppendMode = mode;
            opts.Events.TimeProvider = new SteppingTestClock();
            opts.Events.MetadataConfig.UserNameEnabled = true;
            opts.Events.MetadataConfig.CorrelationIdEnabled = true;
            opts.Events.MetadataConfig.CausationIdEnabled = true;
        });

        var fetched = Guid.NewGuid();
        var appended = Guid.NewGuid();
        var started = Guid.NewGuid();

        await using (var seed = store.LightweightSession())
        {
            seed.Events.StartStream<ProjectedTally>(fetched, new TallyOpened(), new TallyBumped());
            seed.Events.StartStream<ProjectedTally>(appended, new TallyOpened());
            await seed.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await using var session = store.LightweightSession();
        session.CorrelationId = "correlation";
        session.CausationId = "causation";
        session.LastModifiedBy = "leia";

        var stream = await session.Events.FetchForWriting<ProjectedTally>(fetched, TestContext.Current.CancellationToken);
        stream.AppendOne(new TallyBumped());
        stream.AppendOne(new TallyBumped());

        session.Events.StartStream<ProjectedTally>(started, new TallyOpened(), new TallyBumped());

        var preset = Event.For(new TallyBumped());
        preset.Timestamp = new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero);
        preset.CorrelationId = "its own";
        session.Events.Append(appended, preset);

        PendingEventMetadata.Stamp(session);

        var pending = session.PendingChanges.Streams().ToDictionary(x => x.Id);

        var stamped = pending[fetched].Events.Concat(pending[started].Events).ToArray();
        var timestamp = stamped[0].Timestamp;
        (timestamp.Ticks % TimeSpan.TicksPerMicrosecond).ShouldBe(0);
        stamped.ShouldAllBe(x => x.Timestamp == timestamp);

        pending[fetched].Events.Select(x => x.Version).ShouldBe([3L, 4L]);
        pending[started].Events.Select(x => x.Version).ShouldBe([1L, 2L]);
        pending[appended].Events.Single().Version.ShouldBe(0);

        stamped.ShouldAllBe(x => x.CorrelationId == "correlation" && x.CausationId == "causation" && x.UserName == "leia");
        preset.Timestamp.ShouldBe(new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero));
        preset.CorrelationId.ShouldBe("its own");
        preset.CausationId.ShouldBe("causation");

        var projectedFetched = await session.Events.ProjectLatest<ProjectedTally>(fetched, TestContext.Current.CancellationToken);
        var projectedStarted = await session.Events.ProjectLatest<ProjectedTally>(started, TestContext.Current.CancellationToken);
        await session.SaveChangesAsync(TestContext.Current.CancellationToken);

        await using var fresh = store.LightweightSession();
        foreach (var (id, projected) in new[] { (fetched, projectedFetched), (started, projectedStarted) })
        {
            var latest = await fresh.Events.FetchLatest<ProjectedTally>(id, TestContext.Current.CancellationToken);
            latest.ShouldNotBeNull();
            projected.ShouldNotBeNull();
            projected.Version.ShouldBe(latest.Version);
            projected.Count.ShouldBe(latest.Count);
        }

        // Plain Quick writes the server's timestamp for an append to an existing stream
        if (mode != EventAppendMode.Quick)
        {
            var stored = await fresh.Events.FetchStreamAsync(fetched, token: TestContext.Current.CancellationToken);
            stored.Last().Timestamp.ShouldBe(timestamp);
            projectedFetched!.LastBumped.ShouldBe(timestamp);
        }
    }
}

/// <summary>
/// Advances on every read, and never lands on a whole microsecond.
/// </summary>
public class SteppingTestClock : TimeProvider
{
    private long _ticks = new DateTimeOffset(2026, 3, 4, 5, 6, 7, TimeSpan.Zero).Ticks + 7;

    public override DateTimeOffset GetUtcNow()
        => new(Interlocked.Add(ref _ticks, 12_345_670), TimeSpan.Zero);
}

public record TallyOpened;

public record TallyBumped;

public class ProjectedTally
{
    public Guid Id { get; set; }
    public long Version { get; set; }
    public int Count { get; set; }
    public DateTimeOffset LastBumped { get; set; }

    public static ProjectedTally Create(TallyOpened _) => new();

    public void Apply(IEvent<TallyBumped> e)
    {
        Count++;
        LastBumped = e.Timestamp;
    }
}
