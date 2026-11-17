using Fisher;
using JasperFx;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine;
using Wolverine.Fisher;
using Wolverine.Fisher.Requirements;
using Wolverine.Persistence;
using Wolverine.Tracking;

namespace FisherTests;

// GH-5145: [DocumentExists<T>] and [DocumentDoesNotExist<T>] on Fisher, now that all three stores share
// one implementation in core. Fisher had no test of either attribute before.
public class document_existence_attributes_5145 : IAsyncLifetime
{
    private FisherTestDatabase theDatabase = null!;
    private IHost _host = null!;

    public async ValueTask InitializeAsync()
    {
        theDatabase = Servers.CreateDatabase("document_existence_5145");

        _host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Discovery.DisableConventionalDiscovery().IncludeType(typeof(FiExistenceHandler));
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
        var account = new FiExistenceAccount();
        await using var session = _host.Services.GetRequiredService<IDocumentStore>().LightweightSession();
        session.Store(account);
        await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        return account.Id;
    }

    [Fact]
    public async Task document_exists_lets_the_handler_run_when_the_document_is_there()
    {
        var id = await storeAccount();

        var tracked = await _host.InvokeMessageAndWaitAsync(new FiDebitAccount(id));

        tracked.Executed.SingleMessage<FiDebitAccount>().FiExistenceAccountId.ShouldBe(id);
    }

    [Fact]
    public async Task document_exists_refuses_when_the_document_is_missing()
    {
        await Should.ThrowAsync<RequiredDataMissingException>(() =>
            _host.InvokeAsync(new FiDebitAccount(Guid.NewGuid())));
    }

    [Fact]
    public async Task document_does_not_exist_lets_the_handler_run_when_the_document_is_missing()
    {
        var id = Guid.NewGuid();

        var tracked = await _host.InvokeMessageAndWaitAsync(new FiOpenAccount(id));

        tracked.Executed.SingleMessage<FiOpenAccount>().Id.ShouldBe(id);
    }

    [Fact]
    public async Task document_does_not_exist_refuses_when_the_document_is_there()
    {
        var id = await storeAccount();

        await Should.ThrowAsync<RequiredDataMissingException>(() =>
            _host.InvokeAsync(new FiOpenAccount(id)));
    }
}

public class FiExistenceAccount
{
    public Guid Id { get; set; } = Guid.NewGuid();
}

public record FiDebitAccount(Guid FiExistenceAccountId);

public record FiOpenAccount(Guid Id);

public static class FiExistenceHandler
{
    [DocumentExists<FiExistenceAccount>]
    public static void Handle(FiDebitAccount command)
    {
    }

    [DocumentDoesNotExist<FiExistenceAccount>]
    public static void Handle(FiOpenAccount command)
    {
    }
}
