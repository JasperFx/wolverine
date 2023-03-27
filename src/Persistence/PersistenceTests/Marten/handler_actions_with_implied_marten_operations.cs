using IntegrationTests;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine;
using Wolverine.Marten;
using Wolverine.Tracking;
using Xunit;

namespace PersistenceTests.Marten;

public class handler_actions_with_implied_marten_operations : PostgresqlContext, IAsyncLifetime
{
    private IHost _host;
    private IDocumentStore _store;

    public async Task InitializeAsync()
    {
        _host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Services
                    .AddMarten(Servers.PostgresConnectionString)
                    .IntegrateWithWolverine();

                opts.Policies.AutoApplyTransactions();
            }).StartAsync();

        _store = _host.Services.GetRequiredService<IDocumentStore>();

        await _store.Advanced.Clean.DeleteDocumentsByTypeAsync(typeof(NamedDocument));
    }

    public Task DisposeAsync()
    {
        return _host.StopAsync();
    }

    [Fact]
    public async Task add_marten_transaction_behavior_and_op_handling()
    {
        var tracked = await _host.InvokeMessageAndWaitAsync(new CreateMartenDocument("Aubrey"));

        tracked.Sent.MessagesOf<StoreDocument>().ShouldHaveNoMessages();
        tracked.Sent.SingleMessage<MartenMessage2>().Name.ShouldBe("Aubrey");
    }
}

public record CreateMartenDocument(string Name);

public record MartenMessage2(string Name);

public record MartenMessage3(string Name);

public record MartenMessage4(string Name);

public record MartenCommand1(string Name);

public record MartenCommand2(string Name);

public record MartenCommand3(string Name);

public record MartenCommand4(string Name);

public static class MartenCommandHandler
{
    public static (MartenMessage2, StoreDocument) Handle(CreateMartenDocument command)
    {
        return (new MartenMessage2(command.Name), MartenOperations.Store(new NamedDocument { Id = command.Name }));
    }

    public static void Handle(MartenMessage2 message)
    {
        // Nothing yet
    }
}

public class NamedDocument
{
    public string Id { get; set; }
    public int AccessNumber { get; set; }
}