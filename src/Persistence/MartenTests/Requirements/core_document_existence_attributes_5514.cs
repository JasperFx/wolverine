using IntegrationTests;
using JasperFx.Resources;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine;
using Wolverine.Marten;
using Wolverine.Persistence;
using Wolverine.Persistence.Requirements;
using Wolverine.Tracking;

namespace MartenTests.Requirements;

// GH-5514: the store-agnostic [DocumentExists<T>] / [DocumentDoesNotExist<T>] in Wolverine.Persistence.Requirements
// against Marten, including a chain routed to an ancillary store with [Storage]. The obsolete Marten copies are still
// covered by using_data_requirements.
public class core_document_existence_attributes_5514 : PostgresqlContext, IAsyncLifetime
{
    private IHost theHost = null!;

    public async ValueTask InitializeAsync()
    {
        theHost = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Durability.Mode = DurabilityMode.Solo;
                opts.Policies.AutoApplyTransactions();

                opts.Services.AddMarten(m =>
                {
                    m.Connection(Servers.PostgresConnectionString);
                    m.DatabaseSchemaName = "core_existence_main";
                    m.DisableNpgsqlLogging = true;
                }).IntegrateWithWolverine();

                opts.Services.AddMartenStore<ICoreExistenceStore>(m =>
                {
                    m.Connection(Servers.PostgresConnectionString);
                    m.DatabaseSchemaName = "core_existence_ancillary";
                    m.DisableNpgsqlLogging = true;
                }).IntegrateWithWolverine();

                opts.Discovery.DisableConventionalDiscovery()
                    .IncludeType(typeof(CoreExistenceHandler))
                    .IncludeType(typeof(CoreExistenceAncillaryHandler));

                opts.Services.AddResourceSetupOnStartup();
            }).StartAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await theHost.StopAsync();
        theHost.Dispose();
    }

    private async Task<Guid> storeAccount(IDocumentStore store)
    {
        var account = new CoreExistenceAccount();
        await using var session = store.LightweightSession();
        session.Store(account);
        await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        return account.Id;
    }

    private IDocumentStore mainStore => theHost.Services.GetRequiredService<IDocumentStore>();
    private IDocumentStore ancillaryStore => theHost.Services.GetRequiredService<ICoreExistenceStore>();

    [Fact]
    public async Task document_exists_lets_the_handler_run_when_the_document_is_there()
    {
        var id = await storeAccount(mainStore);

        var tracked = await theHost.InvokeMessageAndWaitAsync(new CoreDebitAccount(id));

        tracked.Executed.SingleMessage<CoreDebitAccount>().CoreExistenceAccountId.ShouldBe(id);
    }

    [Fact]
    public async Task document_exists_refuses_when_the_document_is_missing()
    {
        await Should.ThrowAsync<RequiredDataMissingException>(() =>
            theHost.InvokeAsync(new CoreDebitAccount(Guid.NewGuid())));
    }

    [Fact]
    public async Task document_does_not_exist_lets_the_handler_run_when_the_document_is_missing()
    {
        var id = Guid.NewGuid();

        var tracked = await theHost.InvokeMessageAndWaitAsync(new CoreOpenAccount(id));

        tracked.Executed.SingleMessage<CoreOpenAccount>().Id.ShouldBe(id);
    }

    [Fact]
    public async Task document_does_not_exist_refuses_when_the_document_is_there()
    {
        var id = await storeAccount(mainStore);

        await Should.ThrowAsync<RequiredDataMissingException>(() =>
            theHost.InvokeAsync(new CoreOpenAccount(id)));
    }

    [Fact]
    public async Task a_chain_routed_to_an_ancillary_store_checks_that_store()
    {
        var id = await storeAccount(ancillaryStore);

        var tracked = await theHost.InvokeMessageAndWaitAsync(new CoreDebitAncillaryAccount(id));

        tracked.Executed.SingleMessage<CoreDebitAncillaryAccount>().CoreExistenceAccountId.ShouldBe(id);
    }

    [Fact]
    public async Task a_chain_routed_to_an_ancillary_store_does_not_check_the_main_store()
    {
        // The document is only in the main store, so the ancillary-routed check must refuse
        var id = await storeAccount(mainStore);

        await Should.ThrowAsync<RequiredDataMissingException>(() =>
            theHost.InvokeAsync(new CoreDebitAncillaryAccount(id)));
    }
}

public interface ICoreExistenceStore : IDocumentStore;

public class CoreExistenceAccount
{
    public Guid Id { get; set; } = Guid.NewGuid();
}

public record CoreDebitAccount(Guid CoreExistenceAccountId);

public record CoreOpenAccount(Guid Id);

public record CoreDebitAncillaryAccount(Guid CoreExistenceAccountId);

public static class CoreExistenceHandler
{
    [DocumentExists<CoreExistenceAccount>]
    public static void Handle(CoreDebitAccount command)
    {
    }

    [DocumentDoesNotExist<CoreExistenceAccount>]
    public static void Handle(CoreOpenAccount command)
    {
    }
}

[Storage(typeof(ICoreExistenceStore))]
public static class CoreExistenceAncillaryHandler
{
    [DocumentExists<CoreExistenceAccount>]
    public static void Handle(CoreDebitAncillaryAccount command)
    {
    }
}
