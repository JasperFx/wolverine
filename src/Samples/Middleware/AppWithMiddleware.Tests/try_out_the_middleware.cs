using System.Diagnostics;
using Alba;
using Lamar;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Wolverine;
using Wolverine.Runtime;
using Xunit.Abstractions;

namespace AppWithMiddleware.Tests;

public class try_out_the_middleware
{
    private readonly ITestOutputHelper _output;

    public try_out_the_middleware(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public async Task hit()
    {
        await using var host = await AlbaHost.For<Program>();

        var account = new Account
        {
            Balance = 1000
        };

        var store = host.Services.GetRequiredService<IDocumentStore>();
        await using var session = store.LightweightSession();
        session.Store(account);
        await session.SaveChangesAsync();

        var bus = host.Services.GetRequiredService<ICommandBus>();
        await bus.InvokeAsync(new DebitAccount(account.Id, 100));

        var account2 = await session.LoadAsync<Account>(account.Id);
        
        // Should be 1000 + 100
        account2.Balance.ShouldBe(1100);
    }

    [Fact]
    public async Task validation_miss()
    {
        await using var host = await AlbaHost.For<Program>();

        var account = new Account
        {
            Balance = 1000
        };

        var store = host.Services.GetRequiredService<IDocumentStore>();
        await using var session = store.LightweightSession();
        session.Store(account);
        await session.SaveChangesAsync();

        var bus = host.Services.GetRequiredService<ICommandBus>();

        await Should.ThrowAsync<Exception>(async () =>
        {
            await bus.InvokeAsync(new DebitAccount(account.Id, 0));
        });
        
        

    }
}