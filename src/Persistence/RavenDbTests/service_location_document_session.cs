using JasperFx.CodeGeneration.Model;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Raven.Client.Documents;
using Raven.Client.Documents.Session;
using Shouldly;
using Wolverine;
using Wolverine.RavenDb;

namespace RavenDbTests;

/// <summary>
/// GH-4145, the RavenDb half of GH-3001: when a handler chain falls back to service location, a
/// dependency that takes <see cref="IAsyncDocumentSession"/> must receive the SAME outbox-enrolled
/// session the handler is using — not a separate, un-enrolled one (which would defeat the transaction
/// boundary). Proven via reference equality against the handler's own session.
/// </summary>
[Collection("raven")]
public class service_location_document_session
{
    private readonly DatabaseFixture _fixture;

    public service_location_document_session(DatabaseFixture fixture)
    {
        _fixture = fixture;
    }

    private Task<IHost> startHostAsync(IDocumentStore store)
    {
        return Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Durability.Mode = DurabilityMode.Solo;

                opts.Services.AddSingleton(store);
                opts.UseRavenDbPersistence();
                opts.Policies.AutoApplyTransactions();

                opts.Discovery.DisableConventionalDiscovery()
                    .IncludeType(typeof(RavenSessionProbeCommandHandler));

                opts.ServiceLocationPolicy = ServiceLocationPolicy.AllowedButWarn;
                opts.Services.AddScoped<RavenSessionCapturingService>();

                // Force the capturing service to be resolved via service location so the chain creates
                // a child scope — the path GH-4145 primes.
                opts.CodeGeneration.AlwaysUseServiceLocationFor<RavenSessionCapturingService>();
            }).StartAsync(cancellationToken: TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task service_located_session_is_same_instance_as_the_handler_session()
    {
        using var store = _fixture.StartRavenStore();
        using var host = await startHostAsync(store);

        RavenSessionIdentityProbe.Reset();

        var command = new RavenSessionProbeCommand("probes/" + Guid.NewGuid().ToString("N"));
        await host.InvokeAsync(command);

        RavenSessionIdentityProbe.HandlerSession.ShouldNotBeNull();
        RavenSessionIdentityProbe.ServiceLocatedSession.ShouldNotBeNull();

        // Reference equality — the service-located session IS the handler's outbox-enrolled session.
        ReferenceEquals(RavenSessionIdentityProbe.HandlerSession,
                RavenSessionIdentityProbe.ServiceLocatedSession)
            .ShouldBeTrue();

        // And the guarantee that identity is standing in for: the handler never touched the session
        // itself, so the document only lands if the write the service-located session took is inside
        // the transaction the middleware commits.
        using var query = store.OpenAsyncSession();
        (await query.LoadAsync<RavenProbeDoc>(command.Id, TestContext.Current.CancellationToken))
            .ShouldNotBeNull();
    }

    [Fact]
    public async Task ordinary_di_scope_gets_a_genuine_scoped_session()
    {
        using var store = _fixture.StartRavenStore();
        using var host = await startHostAsync(store);

        using var scope = host.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IAsyncDocumentSession>();
        session.ShouldNotBeNull();

        // Scoped, so the same instance within one scope...
        scope.ServiceProvider.GetRequiredService<IAsyncDocumentSession>().ShouldBeSameAs(session);

        // ...and a genuinely different one in another.
        using var other = host.Services.CreateScope();
        other.ServiceProvider.GetRequiredService<IAsyncDocumentSession>().ShouldNotBeSameAs(session);
    }
}

public record RavenSessionProbeCommand(string Id);

public class RavenProbeDoc
{
    public string Id { get; set; } = null!;
}

public static class RavenSessionIdentityProbe
{
    public static IAsyncDocumentSession? HandlerSession;
    public static IAsyncDocumentSession? ServiceLocatedSession;

    public static void Reset()
    {
        HandlerSession = null;
        ServiceLocatedSession = null;
    }
}

public class RavenSessionCapturingService(IAsyncDocumentSession session)
{
    public IAsyncDocumentSession Capture() => session;

    public Task StoreAsync(RavenProbeDoc doc) => session.StoreAsync(doc);
}

public static class RavenSessionProbeCommandHandler
{
    public static async Task Handle(RavenSessionProbeCommand command, IAsyncDocumentSession handlerSession,
        IServiceProvider services)
    {
        var located = services.GetRequiredService<RavenSessionCapturingService>();

        RavenSessionIdentityProbe.HandlerSession = handlerSession;
        RavenSessionIdentityProbe.ServiceLocatedSession = located.Capture();

        // Deliberately writing through the SERVICE-LOCATED session and nothing else
        await located.StoreAsync(new RavenProbeDoc { Id = command.Id });
    }
}
