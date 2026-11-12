using IntegrationTests;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Polecat;
using Shouldly;
using Wolverine;
using Wolverine.Persistence;
using Wolverine.Persistence.Requirements;
using Wolverine.Polecat;
using Wolverine.Tracking;

namespace PolecatTests.Requirements;

// GH-5514: the store-agnostic [DocumentExists<T>] / [DocumentDoesNotExist<T>] in Wolverine.Persistence.Requirements
// against Polecat. The obsolete Polecat copies are still covered by using_data_requirements.
public class core_document_existence_attributes_5514 : IAsyncLifetime
{
    private IHost _host = null!;
    private IDocumentStore _store = null!;

    public async ValueTask InitializeAsync()
    {
        _host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Discovery.DisableConventionalDiscovery().IncludeType(typeof(PcCoreExistenceHandler));
                opts.Durability.Mode = DurabilityMode.Solo;

                opts.Services.AddPolecat(m =>
                    {
                        m.ConnectionString = Servers.SqlServerConnectionString;
                        m.DatabaseSchemaName = "core_existence";
                    })
                    .IntegrateWithWolverine();

                opts.Policies.AutoApplyTransactions();
            }).StartAsync();

        _store = _host.Services.GetRequiredService<IDocumentStore>();
        await ((DocumentStore)_store).Database.ApplyAllConfiguredChangesToDatabaseAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await _host.StopAsync();
        _host.Dispose();
    }

    private async Task<Guid> storeAccount()
    {
        var account = new PcCoreExistenceAccount();
        await using var session = _store.LightweightSession();
        session.Store(account);
        await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        return account.Id;
    }

    [Fact]
    public async Task document_exists_lets_the_handler_run_when_the_document_is_there()
    {
        var id = await storeAccount();

        var tracked = await _host.InvokeMessageAndWaitAsync(new PcCoreDebitAccount(id));

        tracked.Executed.SingleMessage<PcCoreDebitAccount>().PcCoreExistenceAccountId.ShouldBe(id);
    }

    [Fact]
    public async Task document_exists_refuses_when_the_document_is_missing()
    {
        await Should.ThrowAsync<RequiredDataMissingException>(() =>
            _host.InvokeAsync(new PcCoreDebitAccount(Guid.NewGuid())));
    }

    [Fact]
    public async Task document_does_not_exist_lets_the_handler_run_when_the_document_is_missing()
    {
        var id = Guid.NewGuid();

        var tracked = await _host.InvokeMessageAndWaitAsync(new PcCoreOpenAccount(id));

        tracked.Executed.SingleMessage<PcCoreOpenAccount>().Id.ShouldBe(id);
    }

    [Fact]
    public async Task document_does_not_exist_refuses_when_the_document_is_there()
    {
        var id = await storeAccount();

        await Should.ThrowAsync<RequiredDataMissingException>(() =>
            _host.InvokeAsync(new PcCoreOpenAccount(id)));
    }
}

public class PcCoreExistenceAccount
{
    public Guid Id { get; set; } = Guid.NewGuid();
}

public record PcCoreDebitAccount(Guid PcCoreExistenceAccountId);

public record PcCoreOpenAccount(Guid Id);

public static class PcCoreExistenceHandler
{
    [DocumentExists<PcCoreExistenceAccount>]
    public static void Handle(PcCoreDebitAccount command)
    {
    }

    [DocumentDoesNotExist<PcCoreExistenceAccount>]
    public static void Handle(PcCoreOpenAccount command)
    {
    }
}
