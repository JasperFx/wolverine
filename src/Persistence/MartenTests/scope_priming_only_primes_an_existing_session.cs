using IntegrationTests;
using JasperFx.CodeGeneration.Model;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine;
using Wolverine.Attributes;
using Wolverine.Marten;
using Wolverine.Runtime;
using Wolverine.Tracking;
using Xunit;

namespace MartenTests;

/// <summary>
/// GH-4198. The GH-3001 scope priming is documented as self-guarding: a chain with no session of its own
/// primes nothing. It was not. The guard asked <c>TryFindVariable(typeof(IDocumentSession),
/// VariableSource.NotServices)</c>, and a variable source is a FACTORY -- Wolverine.Marten's
/// <c>SessionVariableSource</c> answers that question by building an outbox-enrolled session. So every
/// chain that service-located anything, for any reason, gained a Marten session it never asked for:
/// opened, handed to the priming holder, never read, and never committed. Its cascading messages then
/// left through the un-committed outbox rather than inline, and under sharded multi-tenancy
/// <c>OutboxedSessionFactory.OpenSession</c> threw outright on a handler that touches no database.
/// </summary>
/// <remarks>
/// These assert on the GENERATED SOURCE because that is where the difference lives. Both shapes run and
/// both pass their own assertions -- the manufactured session is invisible at runtime right up until it
/// is not, which is why <see cref="Wolverine.Http.Tests.CodeGeneration"/>'s priming tests could not see
/// it: every one of them uses a probe that genuinely wants a session, where a creating lookup and a
/// passive one give the same answer.
/// </remarks>
public class scope_priming_only_primes_an_existing_session : IAsyncLifetime
{
    private readonly ITestOutputHelper _output;
    private IHost _host = null!;

    public scope_priming_only_primes_an_existing_session(ITestOutputHelper output) => _output = output;

    public async ValueTask InitializeAsync()
    {
        _host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Discovery.DisableConventionalDiscovery()
                    .IncludeType(typeof(NoPersistenceHandler))
                    .IncludeType(typeof(WritesThroughASessionHandler));

                opts.ServiceLocationPolicy = ServiceLocationPolicy.AlwaysAllowed;
                opts.Durability.Mode = DurabilityMode.Solo;

                // An 'opaque' scoped lambda with nothing to do with Marten. Its only effect is that any
                // chain needing it has to service-locate, which creates the child scope that the
                // priming attaches to.
                opts.Services.AddScoped<IOpaqueThing>(_ => new OpaqueThing());

                opts.Services.AddMarten(m =>
                {
                    m.DisableNpgsqlLogging = true;
                    m.Connection(Servers.PostgresConnectionString);
                    m.DatabaseSchemaName = "scope_priming";
                }).IntegrateWithWolverine().UseLightweightSessions();
            }).StartAsync(cancellationToken: TestContext.Current.CancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        await _host.StopAsync();
        _host.Dispose();
    }

    private string sourceFor<T>()
    {
        _host.GetRuntime().Handlers.HandlerFor<T>();
        var chain = _host.GetRuntime().Handlers.ChainFor<T>();
        chain.ShouldNotBeNull();
        chain.SourceCode.ShouldNotBeNull();
        _output.WriteLine(chain.SourceCode);
        return chain.SourceCode;
    }

    [Fact]
    public void a_chain_with_no_persistence_gets_no_session_at_all()
    {
        var code = sourceFor<NoPersistenceCommand>();

        // The scope really is created -- this chain does service-locate...
        code.ShouldContain("_serviceScopeFactory.Create");
        // ...and the MessageContext priming still happens, because that context IS the chain's own
        code.ShouldContain("ScopedMessageContextHolder");

        // ...but nothing here wanted a Marten session, so nothing opens one
        code.ShouldNotContain("OpenSession");
        code.ShouldNotContain("ScopedDocumentSessionHolder");
    }

    [Fact]
    public void a_chain_that_really_has_a_session_is_still_primed_with_it()
    {
        var code = sourceFor<WriteThroughASession>();

        // GH-3001, unchanged: the located IOpaqueThing shares the handler's own enrolled session
        code.ShouldContain("_outboxedSessionFactory.OpenSession");
        code.ShouldContain("ScopedDocumentSessionHolder");

        // ...and exactly one session, not one for the handler and another for the scope
        code.Split("OpenSession").Length.ShouldBe(2);
    }
}

public record NoPersistenceCommand;

public record WriteThroughASession;

public interface IOpaqueThing;

public class OpaqueThing : IOpaqueThing;

[WolverineIgnore]
public static class NoPersistenceHandler
{
    // No Marten anywhere: not a parameter, not a side effect, not [Transactional]
    public static void Handle(NoPersistenceCommand command, IOpaqueThing thing)
    {
    }
}

[WolverineIgnore]
public static class WritesThroughASessionHandler
{
    public static void Handle(WriteThroughASession command, IDocumentSession session, IOpaqueThing thing)
    {
        session.Store(new Part { Name = "widget" });
    }
}
