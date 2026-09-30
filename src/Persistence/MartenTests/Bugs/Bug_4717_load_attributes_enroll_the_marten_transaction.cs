using IntegrationTests;
using JasperFx.CodeGeneration.Frames;
using JasperFx.Resources;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine;
using Wolverine.Attributes;
using Wolverine.Marten;
using Wolverine.Persistence;
using Wolverine.Runtime;
using Wolverine.Runtime.Handlers;
using Wolverine.Tracking;
using Xunit;

namespace MartenTests.Bugs;

// GH-4717, the general form of GH-4712.
//
// Every provider's CanApply answers from chain.ServiceDependencies, which walks only the parameters and
// constructor dependencies of MethodCall frames. Every load frame in the [Entity] family resolves its store
// through IMethodVariables.FindVariable at codegen time instead -- a local in the generated method, never a
// chain dependency -- so the policy and the frames look at two different things that never meet. On a message
// handler there is a second miss: the attributes' Modify() does not run until applyCustomizations, long after
// AutoApplyTransactions has already decided.
//
// GH-4712/#4715 closed it for EF Core, where a change tracker turns the gap into silent data loss. It was
// latent on Marten only because Wolverine's sessions are DocumentTracking.None, so mutating a loaded document
// was never going to persist with or without a commit. That makes Marten the honest place to assert the
// GENERAL fix: the chain must be recognised as transactional and get its commit, even though a Marten user
// would not have lost data from its absence.

public record RenameMartenThing(Guid Id, string Name);

public class MartenThing
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
}

[WolverineIgnore] // Every other host bootstrapped from this assembly uses conventional discovery
public static class RenameMartenThingHandler
{
    // The whole point: the session is never a parameter and never a constructor dependency. It exists only
    // because [Entity] loads through it.
    public static void Handle(RenameMartenThing command, [Entity] MartenThing thing)
    {
        thing.Name = command.Name;
    }
}

[WolverineIgnore]
public static class ListMartenThingsHandler
{
    public static void Handle(ListMartenThings command, [All] IReadOnlyList<MartenThing> things)
    {
        foreach (var thing in things) thing.Name = command.Name;
    }
}

public record ListMartenThings(string Name);

public record RenameMartenThingExplicitly(Guid Id, string Name);

[WolverineIgnore]
public static class RenameMartenThingExplicitlyHandler
{
    // The [Transactional] path rather than AutoApplyTransactions, which resolves its owner through
    // TrySelectTransactionOwner and sets IsTransactional from the answer.
    [Transactional]
    public static void Handle(RenameMartenThingExplicitly command, [Entity] MartenThing thing)
    {
        thing.Name = command.Name;
    }
}

public class Bug_4717_load_attributes_enroll_the_marten_transaction : IAsyncLifetime
{
    private IHost _host = null!;

    public async ValueTask InitializeAsync()
    {
        _host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Discovery.DisableConventionalDiscovery()
                    .IncludeType(typeof(RenameMartenThingHandler))
                    .IncludeType(typeof(ListMartenThingsHandler))
                    .IncludeType(typeof(RenameMartenThingExplicitlyHandler));

                opts.Services.AddMarten(m =>
                {
                    m.Connection(Servers.PostgresConnectionString);
                    m.DatabaseSchemaName = "bug_4717";
                }).IntegrateWithWolverine();

                // THIS is what could not see the [Entity] load
                opts.Policies.AutoApplyTransactions();

                opts.Services.AddResourceSetupOnStartup();
            }).StartAsync();

        await _host.ResetResourceState();
    }

    public async ValueTask DisposeAsync()
    {
        await _host.StopAsync();
        _host.Dispose();
    }

    private HandlerChain chainFor<T>()
    {
        _host.GetRuntime().Handlers.HandlerFor<T>();
        return _host.GetRuntime().Handlers.ChainFor<T>()!;
    }

    [Fact]
    public void the_entity_type_is_discovered_from_the_load_attribute()
    {
        // The core seam, independent of any provider
        chainFor<RenameMartenThing>().DeclarativelyLoadedEntityTypes()
            .ShouldContain(typeof(MartenThing));
    }

    [Fact]
    public void an_all_attribute_unwraps_its_element_type()
    {
        chainFor<ListMartenThings>().DeclarativelyLoadedEntityTypes()
            .ShouldContain(typeof(MartenThing));
    }

    [Fact]
    public void the_chain_is_recognised_as_transactional()
    {
        // THE GH-4717 defect: AutoApplyTransactions asked CanApply, CanApply asked ServiceDependencies, and
        // ServiceDependencies could not see the session the [Entity] frame loads through. So no provider
        // claimed the chain and it was left non-transactional.
        chainFor<RenameMartenThing>().IsTransactional.ShouldBeTrue();
    }

    [Fact]
    public void the_commit_is_actually_generated()
    {
        // IsTransactional alone would pass on a flag set without frames -- see GH-4716.
        chainFor<RenameMartenThing>().Postprocessors.OfType<MethodCall>()
            .ShouldContain(x => x.Method.Name == nameof(IDocumentSession.SaveChangesAsync));
    }

    [Fact]
    public void the_explicit_transactional_path_agrees_with_itself()
    {
        var chain = chainFor<RenameMartenThingExplicitly>();

        // GH-4716 gated IsTransactional on the resolved provider's own CanApply, which was sound only while
        // CanApply was the single gate. GH-4717 claims this chain through CanPersist instead, where CanApply
        // is false -- so the old inference applied a real transaction and then reported the chain as NOT
        // transactional. The flag and the frames have to agree.
        chain.Postprocessors.OfType<MethodCall>()
            .ShouldContain(x => x.Method.Name == nameof(IDocumentSession.SaveChangesAsync));

        chain.IsTransactional.ShouldBeTrue();
    }

    [Fact]
    public async Task the_handler_round_trips()
    {
        var id = Guid.NewGuid();

        await using (var session = _host.Services.GetRequiredService<IDocumentStore>().LightweightSession())
        {
            session.Store(new MartenThing { Id = id, Name = "original" });
            await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await _host.InvokeMessageAndWaitAsync(new RenameMartenThing(id, "renamed"));

        await using var query = _host.Services.GetRequiredService<IDocumentStore>().QuerySession();
        var loaded = await query.LoadAsync<MartenThing>(id, TestContext.Current.CancellationToken);

        // Marten sessions are DocumentTracking.None, so this only persists because the handler's own
        // Storage.Update would -- which is exactly why GH-4712 cost an EF Core user data and cost a Marten
        // user nothing. Asserted at the chain level above; here we only confirm the commit does not throw.
        loaded.ShouldNotBeNull();
    }
}
