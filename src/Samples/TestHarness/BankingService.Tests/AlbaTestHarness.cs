using Alba;
using JasperFx.Core;
using JasperFx.Core.Reflection;
using Microsoft.Extensions.DependencyInjection;
using JasperFx;
using JasperFx.CommandLine;
using Shouldly;
using Wolverine.Tracking;

namespace BankingService.Tests;

public class AlbaTestHarness
{
    // This was built to prove out the fix to https://github.com/JasperFx/wolverine/issues/246

    [Fact]
    public async Task run_end_to_end_with_stub_services()
    {
        JasperFxEnvironment.AutoStartHost = true;
        using var host = await AlbaHost.For<Program>(builder =>
        {
            builder.ConfigureServices(services => services.AddSingleton<IAccountService, StubAccountService>());
        });

        await host.InvokeMessageAndWaitAsync(new DebitAccount(1, 100));

        var stub = host.Services.GetRequiredService<IAccountService>().As<StubAccountService>();
        stub.AccountAmounts[1].ShouldBe(100);
    }
}

public class StubAccountService : IAccountService
{
    public readonly Cache<int, decimal> AccountAmounts = new(x => 0);

    public Task<AccountStatus> DebitAsync(int accountId, decimal amount)
    {
        AccountAmounts[accountId] += amount;
        return Task.FromResult(new AccountStatus(accountId, AccountAmounts[accountId]));
    }
}