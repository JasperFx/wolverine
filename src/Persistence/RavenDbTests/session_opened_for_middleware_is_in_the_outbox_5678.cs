using JasperFx.CodeGeneration;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Raven.Client.Documents;
using Raven.Client.Documents.Session;
using Shouldly;
using Wolverine;
using Wolverine.Attributes;
using Wolverine.Http;
using Wolverine.RavenDb;
using Wolverine.RavenDb.Internals;
using Wolverine.Runtime;

namespace RavenDbTests;

/// <summary>
/// GH-5678. When middleware is the first frame to take an <see cref="IAsyncDocumentSession" />, the session is opened
/// for it ahead of the transactional frame. That session still has to be the one enlisted in the outbox, and it has to
/// be enlisted before the middleware runs, so outgoing messages commit with the document changes
/// </summary>
[Collection("raven")]
public class session_opened_for_middleware_is_in_the_outbox_5678
{
    private readonly DatabaseFixture _fixture;

    public session_opened_for_middleware_is_in_the_outbox_5678(DatabaseFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task compiles_transactional_http_endpoint_when_middleware_takes_session_and_concrete_context()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddSingleton<IDocumentStore>(_ => new DocumentStore
        {
            Urls = ["http://localhost:8080"],
            Database = "codegen"
        }.Initialize());
        builder.Services.AddWolverine(opts =>
        {
            opts.ApplicationAssembly = typeof(EnlistedSession5678Endpoint).Assembly;
            opts.CodeGeneration.TypeLoadMode = TypeLoadMode.Dynamic;
            opts.UseRavenDbPersistence();
        });
        builder.Services.AddWolverineHttp();

        await using var app = builder.Build();
        app.MapWolverineEndpoints(opts => opts.WarmUpRoutes = RouteWarmup.Eager);

        ((IEndpointRouteBuilder)app).DataSources.SelectMany(x => x.Endpoints).OfType<RouteEndpoint>()
            .ShouldContain(x => x.RoutePattern.RawText == "/enlisted-session");
    }

    [Fact]
    public async Task the_session_the_middleware_takes_is_enlisted_before_the_middleware_runs()
    {
        using var store = _fixture.StartRavenStore();

        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Durability.Mode = DurabilityMode.Solo;
                opts.Services.AddSingleton(store);
                opts.UseRavenDbPersistence();
                opts.Policies.AutoApplyTransactions();

                opts.Discovery.DisableConventionalDiscovery().IncludeType(typeof(EnlistedSession5678Handler));
                opts.Policies.AddMiddleware<EnlistedSession5678Middleware>(c => c.MessageType == typeof(StoreEnlistedNote5678));
            }).StartAsync(TestContext.Current.CancellationToken);

        EnlistedSession5678Middleware.EnlistedBeforeMiddleware = null;
        EnlistedSession5678Handler.EnlistedInHandler = null;

        await host.InvokeAsync(new StoreEnlistedNote5678("note-5678"));

        EnlistedSession5678Middleware.EnlistedBeforeMiddleware.ShouldBe(true);
        EnlistedSession5678Handler.EnlistedInHandler.ShouldBe(true);

        // And the transaction is still committed
        using var session = store.OpenAsyncSession();
        (await session.LoadAsync<EnlistedNote5678>("note-5678", TestContext.Current.CancellationToken)).ShouldNotBeNull();
    }
}

public record StoreEnlistedNote5678(string Id);

public static class EnlistedSession5678Endpoint
{
    [WolverinePost("/enlisted-session")]
    [Middleware(typeof(EnlistedSession5678Middleware))]
    [Transactional]
    public static string Post() => "ok";
}

public class EnlistedNote5678
{
    public string Id { get; set; } = null!;
}

public class EnlistedSession5678Middleware
{
    public static bool? EnlistedBeforeMiddleware;

    public static void Before(IAsyncDocumentSession session, MessageContext context)
    {
        EnlistedBeforeMiddleware = context.Transaction is RavenDbEnvelopeTransaction transaction &&
                                   ReferenceEquals(transaction.Session, session);
    }
}

public static class EnlistedSession5678Handler
{
    public static bool? EnlistedInHandler;

    public static async Task Handle(StoreEnlistedNote5678 command, IAsyncDocumentSession session, MessageContext context)
    {
        EnlistedInHandler = context.Transaction is RavenDbEnvelopeTransaction transaction &&
                            ReferenceEquals(transaction.Session, session);

        await session.StoreAsync(new EnlistedNote5678 { Id = command.Id });
    }
}
