using System.Text;
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

// CritterWatch#902: one dead letter row whose `body` was not written by EnvelopeSerializer — raw JSON is
// the observed case, an incompatible serialization version is the other — threw straight out of the
// per-row read and aborted the whole DeadLetterEnvelopeQuery for that database. The console showed
// "No dead letter queue entries found" while 37 perfectly readable dead letters sat behind the poison
// row for 19 days. A bad row must cost you that row, not the queue.
[Collection("marten")]
public class Bug_dlq_unreadable_body : IAsyncLifetime
{
    private IHost _host = null!;
    private IMessageStore _store = null!;

    public async ValueTask InitializeAsync()
    {
        _host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                MartenServiceCollectionExtensions.AddMarten(opts.Services, x =>
                {
                    x.Connection(Servers.PostgresConnectionString);
                    x.DatabaseSchemaName = "dlq_badbody";
                }).IntegrateWithWolverine();

                opts.ListenAtPort(2357).UseDurableInbox();
            }).StartAsync();

        var marten = _host.Services.GetRequiredService<IDocumentStore>();
        await marten.Advanced.Clean.CompletelyRemoveAllAsync();

        _store = _host.Services.GetRequiredService<IWolverineRuntime>().Storage;

        // Marten's clean leaves Wolverine's envelope tables intact, so rows would otherwise accumulate
        // across local runs and the counts below would climb. Same reason as GH-3166's test.
        await _store.Admin.ClearAllAsync();
    }

    public async ValueTask DisposeAsync() => await _host.StopAsync();

    [Fact]
    public async Task one_unreadable_body_does_not_hide_the_rest_of_the_queue()
    {
        // Two readable dead letters, then a third whose body we corrupt to raw JSON.
        var poisoned = await writeDeadLetterAsync();
        await writeDeadLetterAsync();
        await writeDeadLetterAsync();

        await using (var conn = new NpgsqlConnection(Servers.PostgresConnectionString))
        {
            await conn.OpenAsync(TestContext.Current.CancellationToken);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "update dlq_badbody.wolverine_dead_letters set body = @body where id = @id";
            cmd.Parameters.AddWithValue("body", Encoding.UTF8.GetBytes("""{"hello":"world"}"""));
            cmd.Parameters.AddWithValue("id", poisoned);
            await cmd.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }

        // Before the fix this threw out of EnvelopeSerializer.Deserialize and returned nothing at all.
        var results = await _store.DeadLetters.QueryAsync(
            new DeadLetterEnvelopeQuery(TimeRange.AllTime()), CancellationToken.None);

        results.Envelopes.Count.ShouldBe(3);

        // The poison row still comes back, and still identifies itself from the columns that ARE readable.
        var placeholder = results.Envelopes.Single(x => x.Id == poisoned);
        placeholder.Envelope.Message.ShouldBeOfType<PlaceHolder>();
        placeholder.MessageType.ShouldNotBeNullOrEmpty();
        placeholder.ExceptionType.ShouldBe(typeof(DivideByZeroException).FullName);
    }

    [Fact]
    public async Task fetching_the_unreadable_row_by_id_returns_a_placeholder_rather_than_throwing()
    {
        var poisoned = await writeDeadLetterAsync();

        await using (var conn = new NpgsqlConnection(Servers.PostgresConnectionString))
        {
            await conn.OpenAsync(TestContext.Current.CancellationToken);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "update dlq_badbody.wolverine_dead_letters set body = @body where id = @id";
            cmd.Parameters.AddWithValue("body", Encoding.UTF8.GetBytes("not an envelope at all"));
            cmd.Parameters.AddWithValue("id", poisoned);
            await cmd.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }

        var single = await _store.DeadLetters.DeadLetterEnvelopeByIdAsync(poisoned);

        single.ShouldNotBeNull();
        single.Id.ShouldBe(poisoned);
        single.Envelope.Message.ShouldBeOfType<PlaceHolder>();
    }

    private async Task<Guid> writeDeadLetterAsync()
    {
        var envelope = ObjectMother.Envelope();
        envelope.Status = EnvelopeStatus.Incoming;
        await _store.Inbox.StoreIncomingAsync([envelope]);
        await _store.Inbox.MoveToDeadLetterStorageAsync(envelope, new DivideByZeroException("kaboom"));
        return envelope.Id;
    }
}
