using Fisher;
using JasperFx;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine;
using Wolverine.Fisher;
using Wolverine.Persistence.Requirements;
using Wolverine.Persistence;
using Wolverine.Tracking;

namespace FisherTests;

// GH-5514: the store-agnostic [DocumentExists<T>] / [DocumentDoesNotExist<T>] in Wolverine.Persistence.Requirements
// against Fisher. The obsolete Fisher copies are still covered by document_existence_attributes_5145.
public class core_document_existence_attributes_5514 : IAsyncLifetime
{
    private FisherTestDatabase theDatabase = null!;
    private IHost _host = null!;

    public async ValueTask InitializeAsync()
    {
        theDatabase = Servers.CreateDatabase("core_document_existence_5514");

        _host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Discovery.DisableConventionalDiscovery().IncludeType(typeof(FiCoreExistenceHandler));
                opts.Durability.Mode = DurabilityMode.Solo;

                opts.Services.AddFisher(m =>
                    {
                        m.Connection(theDatabase.ConnectionString);
                        m.AutoCreateSchemaObjects = AutoCreate.All;
                    })
                    .ApplyAllDatabaseChangesOnStartup()
                    .IntegrateWithWolverine();

                opts.Policies.AutoApplyTransactions();
            }).StartAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await _host.StopAsync();
        _host.Dispose();
        theDatabase.Dispose();
    }

    private async Task<Guid> storeAccount()
    {
        var account = new FiCoreExistenceAccount();
        await using var session = _host.Services.GetRequiredService<IDocumentStore>().LightweightSession();
        session.Store(account);
        await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        return account.Id;
    }

    [Fact]
    public async Task document_exists_lets_the_handler_run_when_the_document_is_there()
    {
        var id = await storeAccount();

        var tracked = await _host.InvokeMessageAndWaitAsync(new FiCoreDebitAccount(id));

        tracked.Executed.SingleMessage<FiCoreDebitAccount>().FiCoreExistenceAccountId.ShouldBe(id);
    }

    [Fact]
    public async Task document_exists_refuses_when_the_document_is_missing()
    {
        await Should.ThrowAsync<RequiredDataMissingException>(() =>
            _host.InvokeAsync(new FiCoreDebitAccount(Guid.NewGuid())));
    }

    [Fact]
    public async Task document_does_not_exist_lets_the_handler_run_when_the_document_is_missing()
    {
        var id = Guid.NewGuid();

        var tracked = await _host.InvokeMessageAndWaitAsync(new FiCoreOpenAccount(id));

        tracked.Executed.SingleMessage<FiCoreOpenAccount>().Id.ShouldBe(id);
    }

    [Fact]
    public async Task document_does_not_exist_refuses_when_the_document_is_there()
    {
        var id = await storeAccount();

        await Should.ThrowAsync<RequiredDataMissingException>(() =>
            _host.InvokeAsync(new FiCoreOpenAccount(id)));
    }
}

public class FiCoreExistenceAccount
{
    public Guid Id { get; set; } = Guid.NewGuid();
}

public record FiCoreDebitAccount(Guid FiCoreExistenceAccountId);

public record FiCoreOpenAccount(Guid Id);

public static class FiCoreExistenceHandler
{
    [DocumentExists<FiCoreExistenceAccount>]
    public static void Handle(FiCoreDebitAccount command)
    {
    }

    [DocumentDoesNotExist<FiCoreExistenceAccount>]
    public static void Handle(FiCoreOpenAccount command)
    {
    }
}
