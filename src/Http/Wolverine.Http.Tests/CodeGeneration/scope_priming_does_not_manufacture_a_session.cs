using Alba;
using IntegrationTests;
using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Model;
using JasperFx.Core.Reflection;
using Marten;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Wolverine.Marten;
using Wolverine.Runtime;

namespace Wolverine.Http.Tests.CodeGeneration;

/// <summary>
/// GH-4198. The GH-3001 scope priming guarded itself with a CREATING lookup, so the question "does this
/// endpoint have a Marten session?" answered itself by opening one. Every endpoint that service-located
/// anything -- for any reason, with or without persistence -- gained an outbox-enrolled session that is
/// opened, handed to the priming holder, and never read again. Its cascading messages then left through
/// an outbox nothing commits (arriving hundreds of milliseconds later, outside the tracked call that was
/// supposed to wait for them), and under sharded multi-tenancy an endpoint declared as un-tenanted threw
/// out of <c>OutboxedSessionFactory.OpenSession</c> on a database it never touches.
///
/// The sibling tests in <see cref="service_provider_source_compliance"/> could not catch this: their
/// probe genuinely wants an <see cref="IDocumentSession"/>, and where a session is legitimate a creating
/// lookup and a passive one are indistinguishable.
/// </summary>
public class scope_priming_does_not_manufacture_a_session : IAsyncLifetime
{
    private IAlbaHost _host = null!;

    public async ValueTask InitializeAsync()
    {
        var builder = WebApplication.CreateBuilder([]);

        builder.Host.UseWolverine(opts =>
        {
            opts.Discovery.IncludeAssembly(typeof(scope_priming_does_not_manufacture_a_session).Assembly);
            opts.ServiceLocationPolicy = ServiceLocationPolicy.AlwaysAllowed;

            // An opaque scoped lambda with no persistence in it at all. Its only effect on an endpoint
            // that takes it is that the endpoint has to service-locate.
            opts.Services.AddScoped<IPersistenceFreeThing>(_ => new PersistenceFreeThing());
        });

        builder.Services.AddMarten(opts =>
        {
            opts.Connection(Servers.PostgresConnectionString);
            opts.DatabaseSchemaName = "priming_no_session";
            opts.DisableNpgsqlLogging = true;
        }).IntegrateWithWolverine().UseLightweightSessions();

        builder.Services.AddWolverineHttp();

        _host = await AlbaHost.For(builder, app => app.MapWolverineEndpoints(
            opts => opts.ServiceProviderSource = ServiceProviderSource.IsolatedAndScoped));
    }

    public async ValueTask DisposeAsync()
    {
        await _host.StopAsync();
        await _host.DisposeAsync();
    }

    private string sourceFor(string method, string url)
    {
        var chains = _host.Services.GetRequiredService<WolverineHttpOptions>().Endpoints!;
        var chain = chains.ChainFor(method, url);
        chain.ShouldNotBeNull();
        chain.As<ICodeFile>().InitializeSynchronously(chains.Rules, chains, _host.Services);
        chain.SourceCode.ShouldNotBeNull();

        return chain.SourceCode;
    }

    [Fact]
    public void an_endpoint_with_no_persistence_opens_no_session()
    {
        var code = sourceFor("POST", "/priming/no-persistence");

        // The endpoint really does service-locate, so the scope -- and the priming -- are in play...
        code.ShouldContain("_serviceScopeFactory.Create");
        code.ShouldContain("ScopedMessageContextHolder");

        // ...and this is the whole bug: an endpoint that touches no documents, is not [Transactional],
        // and never calls SaveChangesAsync should not be opening a Marten session
        code.ShouldNotContain("OpenSession");
        code.ShouldNotContain("ScopedDocumentSessionHolder");
    }

    [Fact]
    public void an_endpoint_that_uses_a_session_is_still_primed_with_it()
    {
        var code = sourceFor("POST", "/priming/with-session");

        code.ShouldContain("_outboxedSessionFactory.OpenSession");
        code.ShouldContain("ScopedDocumentSessionHolder");
    }
}

public record PrimingCascade;

public interface IPersistenceFreeThing;

public class PersistenceFreeThing : IPersistenceFreeThing;

public static class PrimingEndpoints
{
    [WolverinePost("/priming/no-persistence")]
    public static (IResult, OutgoingMessages) NoPersistence(IPersistenceFreeThing thing)
    {
        return (Results.Ok(), [new PrimingCascade()]);
    }

    [WolverinePost("/priming/with-session")]
    public static IResult WithSession(IPersistenceFreeThing thing, IDocumentSession session)
    {
        session.Store(new PrimingDoc());
        return Results.Ok();
    }

}

// Somewhere for the cascaded message to land
public static class PrimingCascadeHandler
{
    public static void Handle(PrimingCascade cascade)
    {
    }
}

public class PrimingDoc
{
    public Guid Id { get; set; } = Guid.NewGuid();
}
