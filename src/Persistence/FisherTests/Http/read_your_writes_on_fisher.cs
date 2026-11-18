using Alba;
using Fisher;
using JasperFx;
using JasperFx.Events;
using JasperFx.Events.Daemon;
using JasperFx.Events.Projections;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Wolverine;
using Wolverine.Fisher;
using Wolverine.Http;
using Wolverine.Persistence.EventSourcing;

namespace FisherTests.Http;

// GH-5401: [WaitForProjection<T>] against Fisher, mirrored for Marten in Wolverine.Http.Tests and for Polecat in
// PolecatTests. The wait itself is store-agnostic, so this pins the join: the Fisher session's position and the
// Fisher store's progression table.
public class read_your_writes_on_fisher : IAsyncLifetime
{
    private FisherTestDatabase theDatabase = null!;
    private IAlbaHost theHost = null!;

    public async ValueTask InitializeAsync()
    {
        theDatabase = Servers.CreateDatabase("http_ryw");

        var builder = WebApplication.CreateBuilder([]);

        builder.Host.UseWolverine(opts =>
        {
            opts.Durability.Mode = DurabilityMode.Solo;
            opts.Policies.AutoApplyTransactions();

            opts.Discovery.DisableConventionalDiscovery().IncludeType(typeof(FisherRywHandlers));
            opts.Discovery.IncludeAssembly(typeof(read_your_writes_on_fisher).Assembly);
        });

        builder.Services.AddFisher(m =>
            {
                m.Connection(theDatabase.ConnectionString);
                m.AutoCreateSchemaObjects = AutoCreate.All;
                m.Projections.Snapshot<FisherRywPointsView>(SnapshotLifecycle.Async);
            })
            .ApplyAllDatabaseChangesOnStartup()
            .AddAsyncDaemon(DaemonMode.Solo)
            .IntegrateWithWolverine();

        builder.Services.AddWolverineHttp();

        theHost = await AlbaHost.For(builder, app => app.MapWolverineEndpoints(opts =>
            opts.CustomizeHttpEndpointDiscovery(q =>
                q.Excludes.WithCondition("Not a read-your-writes endpoint",
                    type => type != typeof(FisherRywEndpoints)))));
    }

    public async ValueTask DisposeAsync()
    {
        await theHost.DisposeAsync();
        theDatabase.Dispose();
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
                x.Post.Json(new FisherRywAddPoints(id, 3)).ToUrl("/fisher-ryw/points");
                x.StatusCodeShouldBe(204);
            });

            EventStorePosition.TryParse(result.Context.Response.Headers[ReadYourWrites.ConsistencyTokenHeader].ToString(),
                out _).ShouldBeTrue();
            result.Context.Response.Headers.ContainsKey(ReadYourWrites.ProjectionTimeoutHeader).ShouldBeFalse();

            await using var session = theStore.QuerySession();
            (await session.LoadAsync<FisherRywPointsView>(id, TestContext.Current.CancellationToken))
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
            session.Events.StartStream<FisherRywPointsView>(id, new FisherRywPointsAdded(4));
            await session.SaveChangesAsync(TestContext.Current.CancellationToken);
            var token = ((IStorageOperations)session).LastAppendedPosition.ShouldNotBeNull().ToString();

            var result = await theHost.Scenario(x =>
            {
                x.Get.Url($"/fisher-ryw/points/{id}");
                x.WithRequestHeader(ReadYourWrites.ConsistencyTokenHeader, token);
            });

            (await result.ReadAsJsonAsync<FisherRywPointsView>()).ShouldNotBeNull().Total.ShouldBe(4);
        }
    }

    [Fact]
    public async Task a_message_handler_waits_for_the_projection_before_it_completes()
    {
        for (var i = 0; i < 5; i++)
        {
            var id = Guid.NewGuid();
            await theHost.MessageBus().InvokeAsync(new FisherRywAddPointsMessage(id, 6),
                TestContext.Current.CancellationToken);

            await using var session = theStore.QuerySession();
            (await session.LoadAsync<FisherRywPointsView>(id, TestContext.Current.CancellationToken))
                .ShouldNotBeNull().Total.ShouldBe(6);
        }
    }
}

public record FisherRywAddPoints(Guid Id, int Points);

public record FisherRywAddPointsMessage(Guid Id, int Points);

public record FisherRywPointsAdded(int Points);

public class FisherRywPointsView
{
    public Guid Id { get; set; }
    public int Total { get; set; }

    public static FisherRywPointsView Create(FisherRywPointsAdded e) => new() { Total = e.Points };
    public void Apply(FisherRywPointsAdded e) => Total += e.Points;
}

public static class FisherRywEndpoints
{
    [WolverinePost("/fisher-ryw/points")]
    [WaitForProjection<FisherRywPointsView>]
    public static void Post(FisherRywAddPoints command, IDocumentSession session)
        => session.Events.Append(command.Id, new FisherRywPointsAdded(command.Points));

    [WolverineGet("/fisher-ryw/points/{id}")]
    [WaitForProjection<FisherRywPointsView>]
    public static Task<FisherRywPointsView?> Get(Guid id, IQuerySession session)
        => session.LoadAsync<FisherRywPointsView>(id);
}

public static class FisherRywHandlers
{
    [WaitForProjection<FisherRywPointsView>]
    public static void Handle(FisherRywAddPointsMessage command, IDocumentSession session)
        => session.Events.Append(command.Id, new FisherRywPointsAdded(command.Points));
}
