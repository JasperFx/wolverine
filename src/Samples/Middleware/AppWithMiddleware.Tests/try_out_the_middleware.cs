using Alba;
using JasperFx.Core;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using JasperFx;
using JasperFx.CommandLine;
using Shouldly;
using Wolverine;
using Xunit.Abstractions;

namespace AppWithMiddleware.Tests;

public class try_out_the_middleware
{
    private readonly ITestOutputHelper _output;

    public try_out_the_middleware(ITestOutputHelper output)
    {
        // Boo! I blame the AspNetCore team for this one though
        JasperFxEnvironment.AutoStartHost = true;

        _output = output;
    }

    [Fact]
    public async Task the_application_assembly_is_inferred_correctly()
    {
        #region sample_disabling_the_transports_from_web_application_factory

        // This is using Alba to bootstrap a Wolverine application
        // for integration tests, but it's using WebApplicationFactory
        // to do the actual bootstrapping
        await using var host = await AlbaHost.For<Program>(x =>
        {
            // I'm overriding
            x.ConfigureServices(services => services.DisableAllExternalWolverineTransports());
        });

        #endregion


        var options = host.Services.GetRequiredService<WolverineOptions>();
        options.ApplicationAssembly.ShouldBe(typeof(Account).Assembly);
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

        var bus = host.MessageBus();
        await bus.InvokeAsync(new DebitAccount(account.Id, 100));

        var account2 = await session.LoadAsync<Account>(account.Id);

        // Should be 1000 + 100
        account2.Balance.ShouldBe(900);
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

        var bus = host.MessageBus();

        await Should.ThrowAsync<Exception>(async () =>
        {
            await bus.InvokeAsync(new DebitAccount(account.Id, 0));
        });
    }
}

#region sample_when_the_account_is_overdrawn

public class when_the_account_is_overdrawn : IAsyncLifetime
{
    private readonly Account theAccount = new Account
    {
        Balance = 1000,
        MinimumThreshold = 100,
        Id = Guid.NewGuid()
    };

    private readonly TestMessageContext theContext = new TestMessageContext();

    // I happen to like NSubstitute for mocking or dynamic stubs
    private readonly IDocumentSession theDocumentSession = Substitute.For<IDocumentSession>();

    public async Task InitializeAsync()
    {
        var command = new DebitAccount(theAccount.Id, 1200);
        await DebitAccountHandler.Handle(command, theAccount, theDocumentSession, theContext);
    }

    [Fact]
    public void the_account_balance_should_be_negative()
    {
        theAccount.Balance.ShouldBe(-200);
    }

    [Fact]
    public void raises_an_account_overdrawn_message()
    {
        // ShouldHaveMessageOfType() is an extension method in
        // Wolverine itself to facilitate unit testing assertions like this
        theContext.Sent.ShouldHaveMessageOfType<AccountOverdrawn>()
            .AccountId.ShouldBe(theAccount.Id);
    }

    [Fact]
    public void raises_an_overdrawn_deadline_message_in_10_days()
    {
        theContext.ScheduledMessages()
            // Find the wrapping envelope for this message type,
            // then we can chain assertions against the wrapping Envelope
            .ShouldHaveEnvelopeForMessageType<EnforceAccountOverdrawnDeadline>()
            .ScheduleDelay.ShouldBe(10.Days());
    }

    public Task DisposeAsync()
    {
        return Task.CompletedTask;
    }
}

#endregion