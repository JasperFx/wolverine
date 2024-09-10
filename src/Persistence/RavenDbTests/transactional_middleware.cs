using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Raven.Client.Documents;
using Shouldly;
using Wolverine;
using Wolverine.RavenDb;
using Wolverine.Transports.Tcp;

namespace RavenDbTests;

[Collection("raven")]
public class transactional_middleware
{
    private readonly DatabaseFixture _fixture;

    public transactional_middleware(DatabaseFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task use_end_to_end()
    {
        using var store = _fixture.StartRavenStore();

        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Durability.Mode = DurabilityMode.Solo;
                
                opts.Services.AddSingleton(store);
                
                opts.ListenAtPort(2345).UseDurableInbox();
                
                opts.UseRavenDbPersistence();
                opts.Policies.AutoApplyTransactions();
            }).StartAsync();

        await host.InvokeAsync(new RecordTeam("Chiefs", 1960));

        using var session = store.OpenAsyncSession();
        var team = await session.LoadAsync<Team>("Chiefs");
        team.YearFounded.ShouldBe(1960);
    }
}

#region sample_using_ravendb_side_effects

public record RecordTeam(string Team, int Year);

public static class RecordTeamHandler
{
    public static IRavenDbOp Handle(RecordTeam command)
    {
        return RavenOps.Store(new Team { Id = command.Team, YearFounded = command.Year });
    }
}

#endregion

public class Team
{
    public string Id { get; set; }
    public int YearFounded { get; set; }
}