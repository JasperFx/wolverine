using IntegrationTests;
using JasperFx.Core;
using JasperFx.Events.Daemon;
using JasperFx.Events.Projections;
using Marten;
using Marten.Events;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine;
using Wolverine.Marten;
using Wolverine.Tracking;

namespace MartenTests.TestHelpers;

public class reset_data_first : IAsyncLifetime
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
                    m.DatabaseSchemaName = "letters";

                    m.Projections.Add<LetterCountsProjection>(ProjectionLifecycle.Async);
                }).AddAsyncDaemon(DaemonMode.Solo).IntegrateWithWolverine();
                
                opts.Services.AddMartenStore<ILetterStore>(m =>
                {
                    m.DisableNpgsqlLogging = true;
                    
                    m.Connection(Servers.PostgresConnectionString);
                    m.DatabaseSchemaName = "letters2";

                    m.Projections.Add<LetterCountsProjection>(ProjectionLifecycle.Async);
                }).IntegrateWithWolverine();

                opts.Durability.Mode = DurabilityMode.Solo;
            }).StartAsync();
    }

    public async Task DisposeAsync()
    {
        await _host.StopAsync();
    }

    [Fact]
    public async Task reset_all_data_upfront()
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
            .InvokeMessageAndWaitAsync(new AppendLetters(id, ["AAAACCCCBDEEE", "ABCDECCC", "BBBA", "DDDAE"]));
        
        await _host.WaitForNonStaleProjectionDataAsync(5.Seconds());
        
        // Proving that previous data was wiped out

        var all = await session.Query<LetterCounts>().ToListAsync();
        var counts = all.Single();
        counts.Id.ShouldBe(id);
        
        counts.ACount.ShouldBe(7);
        counts.BCount.ShouldBe(5);
        counts.CCount.ShouldBe(8);
    }
    
    [Fact]
    public async Task reset_all_data_upfront_to_ancillary_store()
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
            .InvokeMessageAndWaitAsync(new AppendLetters2(id, ["AAAACCCCBDEEE", "ABCDECCC", "BBBA", "DDDAE"]));
        
        await _host.WaitForNonStaleProjectionDataAsync<ILetterStore>(5.Seconds());
        
        // Proving that previous data was wiped out

        var all = await session.Query<LetterCounts>().ToListAsync();
        var counts = all.Single();
        counts.Id.ShouldBe(id);
        
        counts.ACount.ShouldBe(7);
        counts.BCount.ShouldBe(5);
        counts.CCount.ShouldBe(8);
    }
}