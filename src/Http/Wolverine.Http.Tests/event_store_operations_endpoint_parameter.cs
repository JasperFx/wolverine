using Alba;
using IntegrationTests;
using JasperFx.Events;
using Marten;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine.Marten;

namespace Wolverine.Http.Tests;

// The handler side of this is covered in MartenTests/event_store_operations_parameter. This is the HTTP
// half, because HTTP chains reach AutoApplyTransactions through HttpGraph applying the shared
// IChainPolicy list -- the same CanApply, but worth proving rather than reasoning about, since the whole
// bug being fixed here is that CanApply did not recognize the event operations types and the append then
// vanished with no error.
public class event_store_operations_endpoint_parameter : IAsyncLifetime
{
    private IAlbaHost theHost = null!;

    public async ValueTask InitializeAsync()
    {
        var builder = WebApplication.CreateBuilder([]);

        builder.Services.AddMarten(opts =>
        {
            opts.Connection(Servers.PostgresConnectionString);
            opts.DatabaseSchemaName = "event_store_ops_endpoint";
        }).IntegrateWithWolverine().UseLightweightSessions();

        builder.Host.UseWolverine(opts =>
        {
            opts.Discovery.IncludeAssembly(GetType().Assembly);
            opts.Policies.AutoApplyTransactions();
        });

        builder.Services.AddWolverineHttp();

        theHost = await AlbaHost.For(builder, app =>
        {
            app.UseDeveloperExceptionPage();
            app.MapWolverineEndpoints();
        });
    }

    async ValueTask IAsyncDisposable.DisposeAsync()
    {
        if (theHost != null)
        {
            await theHost.StopAsync();
            theHost.Dispose();
        }
    }

    [Fact]
    public async Task the_endpoint_parameter_is_the_current_sessions_events()
    {
        var id = Guid.NewGuid();

        await theHost.Scenario(x =>
        {
            x.Post.Url($"/api/ledger/{id}/opened");
            x.StatusCodeShouldBe(204);
        });

        var store = theHost.Services.GetRequiredService<IDocumentStore>();
        await using var session = store.LightweightSession();
        var events = await session.Events.FetchStreamAsync(id, token: TestContext.Current.CancellationToken);

        events.Count.ShouldBe(1);
        events[0].Data.ShouldBeOfType<EndpointLedgerOpened>().Note.ShouldBe("opened");
    }
}

public record EndpointLedgerOpened(string Note);

public static class LedgerEndpoint
{
    // Takes the shared JasperFx contract directly -- valid on Marten, Polecat and Fisher alike
    [WolverinePost("/api/ledger/{id}/opened"), EmptyResponse]
    public static void Open(Guid id, IEventStoreOperations events)
    {
        events.StartStream(id, new EndpointLedgerOpened("opened"));
    }
}
