using IntegrationTests;
using JasperFx.Core;
using JasperFx.Resources;
using Marten;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine;
using Wolverine.Marten;
using Wolverine.Postgresql;
using Wolverine.Runtime;
using Wolverine.Tracking;
using Xunit;

namespace MartenTests;

/// <summary>
/// GH-5205, reported on #5208. Marten is registered but NOT integrated with Wolverine, and the messages are persisted
/// in PostgreSQL. <c>[WriteAggregate]</c> still names a Marten provider, which the fallback must not answer with: there
/// is no Marten deduplicator to resolve, so the chain keeps claim-and-release, as it did before the fallback.
/// </summary>
public class deduplication_without_marten_integration : IAsyncLifetime
{
    private const string SchemaName = "tx_dedup_unintegrated";

    private readonly ITestOutputHelper _output;
    private IHost _host = null!;

    public deduplication_without_marten_integration(ITestOutputHelper output)
    {
        _output = output;
    }

    public async ValueTask InitializeAsync()
    {
        _host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Discovery.DisableConventionalDiscovery().IncludeType(typeof(AdjustLedgerItselfHandler));

                opts.Durability.Mode = DurabilityMode.Solo;
                opts.Durability.MessageDeduplicationMode = MessageDeduplicationMode.CompareByHash;
                opts.Durability.DeduplicationWindow = 1.Hours();

                opts.PersistMessagesWithPostgresql(Servers.PostgresConnectionString, SchemaName);

                // No IntegrateWithWolverine()
                opts.Services.AddMarten(m =>
                {
                    m.DisableNpgsqlLogging = true;
                    m.Connection(Servers.PostgresConnectionString);
                    m.DatabaseSchemaName = SchemaName;
                }).UseLightweightSessions();

                opts.Services.AddResourceSetupOnStartup();
            }).StartAsync(cancellationToken: TestContext.Current.CancellationToken);

        await _host.ResetResourceState();
    }

    public async ValueTask DisposeAsync()
    {
        await _host.StopAsync();
        _host.Dispose();
    }

    [Fact]
    public async Task a_write_aggregate_of_the_aggregate_itself_keeps_claim_and_release()
    {
        var ledgerId = Guid.NewGuid();
        await using (var session = _host.DocumentStore().LightweightSession())
        {
            session.Events.StartStream<Ledger>(ledgerId, new LedgerOpened());
            await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await _host.SendMessageAndWaitAsync(new AdjustLedgerItself(ledgerId, 1),
            new DeliveryOptions { DeduplicationId = "unintegrated-1" });
        await _host.SendMessageAndWaitAsync(new AdjustLedgerItself(ledgerId, 2),
            new DeliveryOptions { DeduplicationId = "unintegrated-1" });

        await using var query = _host.DocumentStore().QuerySession();
        var events = await query.Events.FetchStreamAsync(ledgerId, token: TestContext.Current.CancellationToken);
        events.Count(x => x.Data is LedgerAdjusted).ShouldBe(1);

        var chain = _host.GetRuntime().Handlers.ChainFor<AdjustLedgerItself>().ShouldNotBeNull();
        var code = chain.SourceCode.ShouldNotBeNull();
        _output.WriteLine(code);

        code.ShouldContain("TryClaimForEnvelopeAsync");
        code.ShouldContain("ReleaseAsync");
        code.ShouldNotContain(nameof(IMartenDeduplicator.QueueClaim));
    }
}
