using Alba;
using IntegrationTests;
using JasperFx.Events;
using JasperFx.Events.Daemon;
using JasperFx.Events.Projections;
using JasperFx.Resources;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Polecat;
using Shouldly;
using Wolverine;
using Wolverine.Http;
using Wolverine.Persistence.EventSourcing;
using Wolverine.Polecat;

namespace PolecatTests.Http;

// GH-5401: [WaitForProjection<T>] against Polecat, mirrored for Marten in Wolverine.Http.Tests and for Fisher in
// FisherTests. The wait itself is store-agnostic, so this pins the join: the Polecat session's position and the
// Polecat store's progression table.
public class read_your_writes_on_polecat : IAsyncLifetime
{
    private IAlbaHost theHost = null!;

    public async ValueTask InitializeAsync()
    {
        var builder = WebApplication.CreateBuilder([]);

        builder.Host.UseWolverine(opts =>
        {
            opts.Durability.Mode = DurabilityMode.Solo;
            opts.Policies.AutoApplyTransactions();

            opts.Discovery.DisableConventionalDiscovery().IncludeType(typeof(PcRywHandlers));
            opts.Discovery.IncludeAssembly(typeof(read_your_writes_on_polecat).Assembly);
        });

        builder.Services.AddPolecat(m =>
            {
                m.ConnectionString = Servers.SqlServerConnectionString;
                m.DatabaseSchemaName = "pc_ryw";
                m.Projections.Snapshot<PcRywPointsView>(SnapshotLifecycle.Async);
            })
            .IntegrateWithWolverine()
            .UseLightweightSessions()
            .AddAsyncDaemon(DaemonMode.Solo);

        builder.Services.AddResourceSetupOnStartup();
        builder.Services.AddWolverineHttp();

        // No cleaning: every test uses new stream ids, and wiping the events under a running daemon would leave its
        // high water mark ahead of the restarted sequence
        theHost = await AlbaHost.For(builder, app => app.MapWolverineEndpoints(opts =>
            opts.CustomizeHttpEndpointDiscovery(q =>
                q.Excludes.WithCondition("Not a read-your-writes endpoint", type => type != typeof(PcRywEndpoints)))));
    }

    public async ValueTask DisposeAsync()
    {
        await theHost.DisposeAsync();
    }

    private IDocumentStore theStore => theHost.Services.GetRequiredService<IDocumentStore>();

    [Fact]
    public async Task a_write_waits_for_the_projection_and_returns_the_consistency_token()
    {
        for (var i = 0; i < 5; i++)
        {
            var id = Guid.NewGuid();

            var result = await theHost.Scenario(x =>
            {
                x.Post.Json(new PcRywAddPoints(id, 3)).ToUrl("/pc-ryw/points");
                x.StatusCodeShouldBe(204);
            });

            EventStorePosition.TryParse(result.Context.Response.Headers[ReadYourWrites.ConsistencyTokenHeader].ToString(),
                out _).ShouldBeTrue();
            result.Context.Response.Headers.ContainsKey(ReadYourWrites.ProjectionTimeoutHeader).ShouldBeFalse();

            await using var session = theStore.QuerySession();
            (await session.LoadAsync<PcRywPointsView>(id, TestContext.Current.CancellationToken))
                .ShouldNotBeNull().Total.ShouldBe(3);
        }
    }

    [Fact]
    public async Task a_read_waits_for_the_token_from_an_earlier_write()
    {
        for (var i = 0; i < 5; i++)
        {
            var id = Guid.NewGuid();

            await using var session = theStore.LightweightSession();
            session.Events.StartStream<PcRywPointsView>(id, new PcRywPointsAdded(4));
            await session.SaveChangesAsync(TestContext.Current.CancellationToken);
            var token = ((IStorageOperations)session).LastAppendedPosition.ShouldNotBeNull().ToString();

            var result = await theHost.Scenario(x =>
            {
                x.Get.Url($"/pc-ryw/points/{id}");
                x.WithRequestHeader(ReadYourWrites.ConsistencyTokenHeader, token);
            });

            (await result.ReadAsJsonAsync<PcRywPointsView>()).ShouldNotBeNull().Total.ShouldBe(4);
        }
    }

    [Fact]
    public async Task a_message_handler_waits_for_the_projection_before_it_completes()
    {
        for (var i = 0; i < 5; i++)
        {
            var id = Guid.NewGuid();
            await theHost.MessageBus().InvokeAsync(new PcRywAddPointsMessage(id, 6),
                TestContext.Current.CancellationToken);

            await using var session = theStore.QuerySession();
            (await session.LoadAsync<PcRywPointsView>(id, TestContext.Current.CancellationToken))
                .ShouldNotBeNull().Total.ShouldBe(6);
        }
    }
}

public record PcRywAddPoints(Guid Id, int Points);

public record PcRywAddPointsMessage(Guid Id, int Points);

public record PcRywPointsAdded(int Points);

public class PcRywPointsView
{
    public Guid Id { get; set; }
    public int Total { get; set; }

    public static PcRywPointsView Create(PcRywPointsAdded e) => new() { Total = e.Points };
    public void Apply(PcRywPointsAdded e) => Total += e.Points;
}

public static class PcRywEndpoints
{
    [WolverinePost("/pc-ryw/points")]
    [WaitForProjection<PcRywPointsView>]
    public static void Post(PcRywAddPoints command, IDocumentSession session)
        => session.Events.Append(command.Id, new PcRywPointsAdded(command.Points));

    [WolverineGet("/pc-ryw/points/{id}")]
    [WaitForProjection<PcRywPointsView>]
    public static Task<PcRywPointsView?> Get(Guid id, IQuerySession session)
        => session.LoadAsync<PcRywPointsView>(id);
}

public static class PcRywHandlers
{
    [WaitForProjection<PcRywPointsView>]
    public static void Handle(PcRywAddPointsMessage command, IDocumentSession session)
        => session.Events.Append(command.Id, new PcRywPointsAdded(command.Points));
}
