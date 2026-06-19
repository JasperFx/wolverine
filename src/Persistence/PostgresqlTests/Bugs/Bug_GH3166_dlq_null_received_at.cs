using IntegrationTests;
using JasperFx.Core;
using Marten;
using Npgsql;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine;
using Wolverine.ComplianceTests;
using Wolverine.Marten;
using Wolverine.Persistence.Durability;
using Wolverine.Persistence.Durability.DeadLetterManagement;
using Wolverine.Runtime;
using Wolverine.Transports.Tcp;
using Xunit;

namespace PostgresqlTests.Bugs;

// GH-3166: a dead letter is written with received_at = envelope.Destination?.ToString(), so received_at is
// NULL for any envelope that dead-lettered without a Destination (e.g. a locally-published message that
// failed before routing). The DLQ admin readers (SummarizeAllAsync + ReadDeadLetterAsync) read received_at
// as a non-null string, so a SINGLE destination-less dead letter threw on the DBNull and aborted the whole
// read — the DLQ explorer reported "No dead letter queue entries found" / "No messages loaded" for the
// entire store even though count(*) (the durability monitor's path) reported hundreds. (CritterWatch ProductSupport#21)
[Collection("marten")]
public class Bug_GH3166_dlq_null_received_at : IAsyncLifetime
{
    private IHost _host = null!;
    private IMessageStore _store = null!;

    public async Task InitializeAsync()
    {
        _host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                MartenServiceCollectionExtensions.AddMarten(opts.Services, x =>
                {
                    x.Connection(Servers.PostgresConnectionString);
                    x.DatabaseSchemaName = "dlq_nullrecv";
                }).IntegrateWithWolverine();

                opts.ListenAtPort(2356).UseDurableInbox();
            }).StartAsync();

        var marten = _host.Services.GetRequiredService<IDocumentStore>();
        await marten.Advanced.Clean.CompletelyRemoveAllAsync();

        _store = _host.Services.GetRequiredService<IWolverineRuntime>().Storage;
    }

    public async Task DisposeAsync() => await _host.StopAsync();

    [Fact]
    public async Task summarize_and_query_tolerate_a_null_received_at()
    {
        // Write a dead letter normally (valid Destination → valid body), then null out received_at directly
        // to reproduce the destination-less row that the write path persists as received_at = NULL.
        var envelope = ObjectMother.Envelope();
        envelope.Status = EnvelopeStatus.Incoming;
        await _store.Inbox.StoreIncomingAsync(new[] { envelope });
        await _store.Inbox.MoveToDeadLetterStorageAsync(envelope, new DivideByZeroException("kaboom"));

        await using (var conn = new NpgsqlConnection(Servers.PostgresConnectionString))
        {
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "update dlq_nullrecv.wolverine_dead_letters set received_at = null";
            await cmd.ExecuteNonQueryAsync();
        }

        // Both of these threw on the DBNull received_at before the fix.
        var summary = await _store.DeadLetters.SummarizeAllAsync("svc", TimeRange.AllTime(), CancellationToken.None);
        summary.Sum(x => x.Count).ShouldBe(1);

        var results = await _store.DeadLetters.QueryAsync(
            new DeadLetterEnvelopeQuery(TimeRange.AllTime()), CancellationToken.None);
        results.Envelopes.Count.ShouldBe(1);
    }
}
