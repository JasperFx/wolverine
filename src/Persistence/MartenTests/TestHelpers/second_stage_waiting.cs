using System.Diagnostics;
using IntegrationTests;
using JasperFx.Core;
using JasperFx.Events;
using JasperFx.Events.Daemon;
using JasperFx.Events.Projections;
using Marten;
using Marten.Events;
using Marten.Events.Aggregation;
using MartenTests.AggregateHandlerWorkflow;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine;
using Wolverine.Marten;
using Wolverine.Tracking;

namespace MartenTests.TestHelpers;

public class second_stage_waiting : IAsyncLifetime
{
    private IHost _host;

    public async Task InitializeAsync()
    {

        _host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Services.AddMarten(m =>
                {
                    m.DisableNpgsqlLogging = true;
                    
                    m.Connection(Servers.PostgresConnectionString);
                    m.DatabaseSchemaName = "letters3";

                    m.Projections.Add<LetterCountsProjectionWithSideEffects>(ProjectionLifecycle.Async);
                }).AddAsyncDaemon(DaemonMode.Solo).IntegrateWithWolverine();
                
                opts.Services.AddMartenStore<ILetterStore>(m =>
                {
                    m.DisableNpgsqlLogging = true;
                    
                    m.Connection(Servers.PostgresConnectionString);
                    m.DatabaseSchemaName = "letters4";

                    m.Projections.Add<LetterCountsProjectionWithSideEffects>(ProjectionLifecycle.Async);
                }).IntegrateWithWolverine();

                opts.Durability.Mode = DurabilityMode.Solo;
            }).StartAsync();
    }

    public async Task DisposeAsync()
    {
        await _host.StopAsync();
    }
    
    [Fact]
    public async Task with_main_store()
    {
        await _host.ResetAllMartenDataAsync();
        
        var id = Guid.NewGuid();
        
        // Setting up some other aggregates first
        using var session = _host.DocumentStore().LightweightSession();
        session.Events.StartStream<LetterCounts>("AABBCCDDEE".ToLetterEvents());
        session.Events.StartStream<LetterCounts>("AABBCCDDEE".ToLetterEvents());
        session.Events.StartStream<LetterCounts>("AABBCCDDEE".ToLetterEvents());
        await session.SaveChangesAsync();
        await _host.WaitForNonStaleProjectionDataAsync(5.Seconds());
        
        (await session.Query<LetterCounts>().CountAsync()).ShouldBe(3);


        var tracked = await _host.TrackActivity()
            .ResetAllMartenDataFirst()
            .PauseThenCatchUpOnMartenDaemonActivity(CatchUpMode.AndDoNothing)
            .InvokeMessageAndWaitAsync(new AppendLetters(id, ["AAAACCCCBDEEE", "ABCDECCC", "BBBA", "DDDAE"]));

        // Proving that previous data was wiped out

        var all = await session.Query<LetterCounts>().ToListAsync();
        var counts = all.Single();
        counts.Id.ShouldBe(id);
        
        counts.ACount.ShouldBe(7);
        counts.BCount.ShouldBe(5);
        counts.CCount.ShouldBe(8);
        
        tracked.Executed.SingleMessage<GotFive>().Id.ShouldBe(id);
        tracked.Executed.SingleMessage<GotFiveResponse>().Id.ShouldBe(id);
    }
    
    [Fact]
    public async Task with_ancillary_store()
    {
        await _host.ResetAllMartenDataAsync<ILetterStore>();
        
        var id = Guid.NewGuid();
        
        // Setting up some other aggregates first
        using var session = _host.DocumentStore<ILetterStore>().LightweightSession();
        session.Events.StartStream<LetterCounts>("AABBCCDDEE".ToLetterEvents());
        session.Events.StartStream<LetterCounts>("AABBCCDDEE".ToLetterEvents());
        session.Events.StartStream<LetterCounts>("AABBCCDDEE".ToLetterEvents());
        await session.SaveChangesAsync();
        await _host.WaitForNonStaleProjectionDataAsync<ILetterStore>(5.Seconds());
        
        (await session.Query<LetterCounts>().CountAsync()).ShouldBe(3);


        var tracked = await _host.TrackActivity()
            .ResetAllMartenDataFirst<ILetterStore>()
            .Timeout(20.Seconds())
            .PauseThenCatchUpOnMartenDaemonActivity<ILetterStore>()
            .InvokeMessageAndWaitAsync(new AppendLetters2(id, ["AAAACCCCBDEEE", "ABCDECCC", "BBBA", "DDDAE"]));
        
        // Proving that previous data was wiped out

        var all = await session.Query<LetterCounts>().ToListAsync();
        var counts = all.Single();
        counts.Id.ShouldBe(id);
        
        counts.ACount.ShouldBe(7);
        counts.BCount.ShouldBe(5);
        counts.CCount.ShouldBe(8);
        
        tracked.Executed.SingleMessage<GotFive>().Id.ShouldBe(id);
        tracked.Executed.SingleMessage<GotFiveResponse>().Id.ShouldBe(id);
    }
}


public record GotFive(Guid Id);

public record GotFiveResponse(Guid Id);

public static class GotFiveHandler
{
    public static GotFiveResponse Handle(GotFive message) => new GotFiveResponse(message.Id);

    public static void Handle(GotFiveResponse m) => Debug.WriteLine("Got five response for " + m.Id);
}

public class LetterCountsProjectionWithSideEffects: SingleStreamProjection<LetterCounts, Guid>
{
    public override LetterCounts Evolve(LetterCounts snapshot, Guid id, IEvent e)
    {

        switch (e.Data)
        {
            case AEvent _:
                snapshot ??= new() { Id = id };
                snapshot.ACount++;
                break;

            case BEvent _:
                snapshot ??= new() { Id = id };
                snapshot.BCount++;
                break;

            case CEvent _:
                snapshot ??= new() { Id = id };
                snapshot.CCount++;
                break;

            case DEvent _:
                snapshot ??= new() { Id = id };
                snapshot.DCount++;
                break;
            
            case EEvent _:
                snapshot ??= new() { Id = id };
                snapshot.ECount++;
                break;
        }

        return snapshot;
    }

    public override ValueTask RaiseSideEffects(IDocumentOperations operations, IEventSlice<LetterCounts> slice)
    {
        if (slice.Snapshot.ECount >= 5)
        {
            slice.PublishMessage(new GotFive(slice.Snapshot.Id));
        }

        return new ValueTask();
    }
}