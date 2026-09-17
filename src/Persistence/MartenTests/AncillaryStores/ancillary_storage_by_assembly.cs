using IntegrationTests;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using JasperFx.Resources;
using Shouldly;
using Wolverine;
using Wolverine.Marten;
using Wolverine.Persistence;
using Wolverine.Tracking;

namespace MartenTests.AncillaryStores;

// GH-4477. The end-to-end half of UseAncillaryStorageFromAssembly: the CoreTests suite pins WHICH chains
// the policy picks, and this pins that the routing decision actually moves the transaction -- the
// handler opens and commits through the ancillary store's outbox-enrolled session and the main store
// never sees the document. Same bar the [Storage] tests next door hold, without the per-handler markup.
//
// Note the handlers here carry NO storage attribute at all. That is the whole point.
public class ancillary_storage_by_assembly : IAsyncLifetime
{
    private IHost theHost = null!;

    public async ValueTask InitializeAsync()
    {
        theHost = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Durability.MessageStorageSchemaName = "wolverine";
                opts.Durability.Mode = DurabilityMode.Solo;
                opts.Policies.AutoApplyTransactions();

                opts.Services.AddMarten(m =>
                {
                    m.Connection(Servers.PostgresConnectionString);
                    m.DatabaseSchemaName = "by_assembly_main";
                    m.Events.DatabaseSchemaName = "by_assembly_main";
                }).IntegrateWithWolverine();

                opts.Services.AddMartenStore<IByAssemblyModuleStore>(m =>
                    {
                        m.Connection(Servers.PostgresConnectionString);
                        m.DatabaseSchemaName = "by_assembly_module";
                        m.Events.DatabaseSchemaName = "by_assembly_module";
                    })
                    .IntegrateWithWolverine();

                // A SECOND ancillary store, so the precedence test can prove the attribute wins by
                // landing the document somewhere the policy would never have sent it.
                opts.Services.AddMartenStore<IByAssemblyOtherStore>(m =>
                    {
                        m.Connection(Servers.PostgresConnectionString);
                        m.DatabaseSchemaName = "by_assembly_other";
                        m.Events.DatabaseSchemaName = "by_assembly_other";
                    })
                    .IntegrateWithWolverine();

                // Scoping to this test assembly would sweep in every other handler here, so discovery
                // is narrowed first -- the policy then covers exactly the two handlers below.
                opts.Discovery.DisableConventionalDiscovery()
                    .IncludeType(typeof(ByAssemblyModuleHandler))
                    .IncludeType(typeof(ByAssemblyOptOutHandler));

                #region sample_use_ancillary_storage_from_assembly
                // Every message handler, HTTP endpoint and gRPC service in this assembly commits
                // through the IByAssemblyModuleStore ancillary store -- no per-handler attributes.
                opts.Policies.UseAncillaryStorageFromAssembly(typeof(IByAssemblyModuleStore),
                    typeof(ByAssemblyModuleHandler).Assembly);

                // ...or name the module by one of its types instead of its Assembly:
                //     opts.Policies.UseAncillaryStorageFromAssemblyContaining<SomeModuleType>(
                //         typeof(IByAssemblyModuleStore));
                #endregion

                opts.Services.AddResourceSetupOnStartup();
            }).StartAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await theHost.StopAsync();
        theHost.Dispose();
    }

    [Fact]
    public async Task an_unannotated_handler_commits_through_the_module_store()
    {
        var message = new ByAssemblyModuleMessage(Guid.NewGuid().ToString());
        await theHost.InvokeMessageAndWaitAsync(message);

        var store = theHost.DocumentStore<IByAssemblyModuleStore>();
        await using var session = store.QuerySession();
        (await session.LoadAsync<ByAssemblyRecord>(message.Id, TestContext.Current.CancellationToken))
            .ShouldNotBeNull("The assembly-wide policy should have routed this handler to the module store.");

        // ...and the main store never saw it. Without this the test would still pass if the policy were
        // writing to both.
        var mainStore = theHost.Services.GetRequiredService<IDocumentStore>();
        await using var mainSession = mainStore.QuerySession();
        (await mainSession.LoadAsync<ByAssemblyRecord>(message.Id, TestContext.Current.CancellationToken))
            .ShouldBeNull();
    }

    [Fact]
    public async Task an_explicitly_annotated_handler_overrides_its_modules_default()
    {
        // The attribute names a DIFFERENT store than the policy, so where the document lands is the
        // whole answer: module store means the policy won, other store means the attribute did.
        var message = new ByAssemblyOptOutMessage(Guid.NewGuid().ToString());
        await theHost.InvokeMessageAndWaitAsync(message);

        var otherStore = theHost.DocumentStore<IByAssemblyOtherStore>();
        await using var otherSession = otherStore.QuerySession();
        (await otherSession.LoadAsync<ByAssemblyRecord>(message.Id, TestContext.Current.CancellationToken))
            .ShouldNotBeNull("An explicit [MartenStore] must override the assembly-wide default.");

        var moduleStore = theHost.DocumentStore<IByAssemblyModuleStore>();
        await using var moduleSession = moduleStore.QuerySession();
        (await moduleSession.LoadAsync<ByAssemblyRecord>(message.Id, TestContext.Current.CancellationToken))
            .ShouldBeNull("The assembly-wide policy must have left this handler alone entirely.");
    }
}

public interface IByAssemblyModuleStore : IDocumentStore;

public interface IByAssemblyOtherStore : IDocumentStore;

public class ByAssemblyRecord
{
    public string Id { get; set; } = null!;
}

public record ByAssemblyModuleMessage(string Id);

public record ByAssemblyOptOutMessage(string Id);

// No attribute anywhere -- routed purely by UseAncillaryStorageFromAssemblyContaining.
public static class ByAssemblyModuleHandler
{
    public static void Handle(ByAssemblyModuleMessage message, IDocumentSession session)
    {
        session.Store(new ByAssemblyRecord { Id = message.Id });
    }
}

// Carries an explicit attribute, which must take precedence over the assembly-wide default rather than
// stacking a second outbox frame on top of it.
public static class ByAssemblyOptOutHandler
{
    [MartenStore(typeof(IByAssemblyOtherStore))]
    public static void Handle(ByAssemblyOptOutMessage message, IDocumentSession session)
    {
        session.Store(new ByAssemblyRecord { Id = message.Id });
    }
}
