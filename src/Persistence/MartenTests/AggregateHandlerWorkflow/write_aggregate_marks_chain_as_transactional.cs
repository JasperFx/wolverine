using IntegrationTests;
using JasperFx.CodeGeneration.Frames;
using Marten;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine;
using Wolverine.Marten;
using Wolverine.Tracking;

namespace MartenTests.AggregateHandlerWorkflow;

// GH-3893: a chain whose only Marten shape is [WriteAggregate] gets a SaveChangesAsync
// postprocessor from the aggregate handler workflow, but the workflow never marked the chain
// as transactional. AutoApplyTransactions doesn't cover the gap either - CanApply deliberately
// ignores [WriteAggregate] because this workflow applies transaction support itself - so the flag
// and the generated code disagreed. This pins the two together.
public class write_aggregate_marks_chain_as_transactional : IAsyncLifetime
{
    private IHost theHost = null!;

    public async ValueTask InitializeAsync()
    {
        theHost = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Discovery.DisableConventionalDiscovery().IncludeType(typeof(PostLedgerEntryHandler));
                opts.Durability.Mode = DurabilityMode.Solo;
                opts.Services.AddMarten(m =>
                {
                    m.Connection(Servers.PostgresConnectionString);
                    m.DatabaseSchemaName = "write_aggregate_transactional";
                }).IntegrateWithWolverine();

                // Deliberately NOT calling AutoApplyTransactions(). The aggregate handler workflow
                // commits on its own, so the flag has to be right without that policy too.
            }).StartAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await theHost.StopAsync();
        theHost.Dispose();
    }

    [Fact]
    public async Task chain_is_transactional_and_actually_commits()
    {
        var streamId = Guid.NewGuid();
        await using (var session = theHost.DocumentStore().LightweightSession())
        {
            session.Events.StartStream<Ledger>(streamId, new LedgerEntryPosted(10m));
            await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await theHost.InvokeAsync(new PostLedgerEntry(streamId, 5m));

        // The generated code really did commit the appended event
        await using (var session = theHost.DocumentStore().LightweightSession())
        {
            var ledger = await session.Events.AggregateStreamAsync<Ledger>(streamId,
                token: TestContext.Current.CancellationToken);
            ledger!.Balance.ShouldBe(15m);
        }

        var chain = theHost.GetRuntime().Handlers.ChainFor<PostLedgerEntry>()!;

        chain.Postprocessors.OfType<MethodCall>()
            .Any(x => x.Method.Name == nameof(IDocumentSession.SaveChangesAsync))
            .ShouldBeTrue();

        chain.IsTransactional.ShouldBeTrue();
    }
}

public record LedgerEntryPosted(decimal Amount);

public record PostLedgerEntry(Guid LedgerId, decimal Amount);

public class Ledger
{
    public Guid Id { get; set; }
    public decimal Balance { get; set; }

    public void Apply(LedgerEntryPosted posted)
    {
        Balance += posted.Amount;
    }
}

public static class PostLedgerEntryHandler
{
    public static LedgerEntryPosted Handle(PostLedgerEntry command, [WriteAggregate] Ledger ledger)
    {
        return new LedgerEntryPosted(command.Amount);
    }
}
