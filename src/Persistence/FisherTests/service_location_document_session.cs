using Fisher;
using JasperFx;
using JasperFx.CodeGeneration.Model;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine;
using Wolverine.Fisher;
using Wolverine.Tracking;

namespace FisherTests;

/// <summary>
/// GH-4145 (the GH-3001 pattern, ported from Wolverine.Marten): when a handler chain falls back to
/// service location, a dependency that takes <see cref="IDocumentSession"/> must receive the SAME
/// outbox-enrolled session the handler is using — not a separate, un-enrolled one (which would defeat
/// the transaction boundary). Proven via reference equality against the handler's own session.
/// </summary>
public class service_location_document_session : IAsyncLifetime
{
    private FisherTestDatabase theDatabase = null!;
    private IHost _host = null!;

    public async ValueTask InitializeAsync()
    {
        theDatabase = Servers.CreateDatabase("service_location");

        _host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Durability.Mode = DurabilityMode.Solo;

                opts.Services.AddFisher(m =>
                    {
                        m.Connection(theDatabase.ConnectionString);
                        m.AutoCreateSchemaObjects = AutoCreate.All;
                    })
                    .ApplyAllDatabaseChangesOnStartup()
                    .IntegrateWithWolverine();

                opts.Policies.AutoApplyTransactions();

                opts.Discovery.DisableConventionalDiscovery()
                    .IncludeType(typeof(FiSessionProbeCommandHandler));

                opts.ServiceLocationPolicy = ServiceLocationPolicy.AllowedButWarn;
                opts.Services.AddScoped<FiSessionCapturingService>();

                // Force the capturing service to be resolved via service location so the chain creates
                // a child scope — the path GH-4145 primes.
                opts.CodeGeneration.AlwaysUseServiceLocationFor<FiSessionCapturingService>();
            }).StartAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await _host.StopAsync();
        _host.Dispose();
        theDatabase.Dispose();
    }

    [Fact]
    public async Task service_located_session_is_same_instance_as_the_handler_session()
    {
        FiSessionIdentityProbe.Reset();

        var command = new FiSessionProbeCommand(Guid.NewGuid());
        await _host.InvokeMessageAndWaitAsync(command);

        FiSessionIdentityProbe.HandlerSession.ShouldNotBeNull();
        FiSessionIdentityProbe.ServiceLocatedSession.ShouldNotBeNull();

        // Reference equality — the service-located session IS the handler's outbox-enrolled session.
        ReferenceEquals(FiSessionIdentityProbe.HandlerSession, FiSessionIdentityProbe.ServiceLocatedSession)
            .ShouldBeTrue();

        // And the guarantee that identity is standing in for: the handler never touched the session
        // itself, so the document only lands if the write the service-located session took is inside
        // the transaction the middleware commits.
        var store = _host.Services.GetRequiredService<IDocumentStore>();
        await using var query = store.QuerySession();
        (await query.LoadAsync<FiProbeDoc>(command.Id, TestContext.Current.CancellationToken))
            .ShouldNotBeNull();
    }

    [Fact]
    public void ordinary_di_scope_gets_a_genuine_scoped_session()
    {
        using var scope = _host.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        session.ShouldNotBeNull();

        // Scoped, so the same instance within one scope...
        scope.ServiceProvider.GetRequiredService<IDocumentSession>().ShouldBeSameAs(session);

        // ...and a genuinely different one in another.
        using var other = _host.Services.CreateScope();
        other.ServiceProvider.GetRequiredService<IDocumentSession>().ShouldNotBeSameAs(session);
    }
}

public record FiSessionProbeCommand(Guid Id);

public class FiProbeDoc
{
    public Guid Id { get; set; }
}

public static class FiSessionIdentityProbe
{
    public static IDocumentSession? HandlerSession;
    public static IDocumentSession? ServiceLocatedSession;

    public static void Reset()
    {
        HandlerSession = null;
        ServiceLocatedSession = null;
    }
}

public class FiSessionCapturingService(IDocumentSession session)
{
    public IDocumentSession Capture() => session;

    public void Store(FiProbeDoc doc) => session.Store(doc);
}

public static class FiSessionProbeCommandHandler
{
    public static void Handle(FiSessionProbeCommand command, IDocumentSession handlerSession,
        IServiceProvider services)
    {
        var located = services.GetRequiredService<FiSessionCapturingService>();

        FiSessionIdentityProbe.HandlerSession = handlerSession;
        FiSessionIdentityProbe.ServiceLocatedSession = located.Capture();

        // Deliberately writing through the SERVICE-LOCATED session and nothing else
        located.Store(new FiProbeDoc { Id = command.Id });
    }
}
