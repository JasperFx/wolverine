using IntegrationTests;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Weasel.SqlServer;
using Wolverine;
using Wolverine.ComplianceTests;
using Wolverine.Persistence.Durability;
using Wolverine.RDBMS;
using Wolverine.SqlServer;

namespace SqlServerTests.Persistence;

/// <summary>
/// GH-5423. The in-transaction mark-as-handled, which EF Core's envelope transaction runs inside the handler's own
/// transaction, refuses to let a handler commit work that another execution already finished -- the case of a node
/// that was ejected as stale, resumed, and finished envelopes whose rows had been recovered and handled elsewhere.
/// </summary>
public class Bug_5423_mark_as_handled_refuses_work_handled_elsewhere : IAsyncLifetime
{
    private const string Schema = "gh5423";
    private IHost _host = null!;
    private IMessageDatabase _database = null!;

    public async ValueTask InitializeAsync()
    {
        await using (var conn = new SqlConnection(Servers.SqlServerConnectionString))
        {
            await conn.OpenAsync(TestContext.Current.CancellationToken);
            await conn.DropSchemaAsync(Schema, TestContext.Current.CancellationToken);
        }

        _host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Durability.Mode = DurabilityMode.Solo;
                opts.PersistMessagesWithSqlServer(Servers.SqlServerConnectionString, Schema);
            }).StartAsync(TestContext.Current.CancellationToken);

        _database = (IMessageDatabase)_host.Services.GetRequiredService<IMessageStore>();
    }

    public async ValueTask DisposeAsync()
    {
        await _host.StopAsync(TestContext.Current.CancellationToken);
        _host.Dispose();
    }

    private async Task<Envelope> storeIncomingAsync()
    {
        var envelope = ObjectMother.Envelope();
        envelope.Status = EnvelopeStatus.Incoming;
        envelope.Destination = new Uri("local://gh5423");
        await _database.Inbox.StoreIncomingAsync(envelope);
        return envelope;
    }

    private async Task markInTransactionAsync(Envelope envelope)
    {
        await using var conn = new SqlConnection(Servers.SqlServerConnectionString);
        await conn.OpenAsync();
        await using var tx = await conn.BeginTransactionAsync();

        await _database.MarkIncomingEnvelopeAsHandledInTransactionAsync(conn, tx, envelope,
            DateTimeOffset.UtcNow.AddMinutes(5), CancellationToken.None);

        await tx.CommitAsync();
    }

    private async Task<EnvelopeStatus?> statusOfAsync(Envelope envelope)
    {
        return (await _database.Admin.AllIncomingAsync()).SingleOrDefault(x => x.Id == envelope.Id)?.Status;
    }

    [Fact]
    public async Task an_incoming_row_is_marked_handled()
    {
        var envelope = await storeIncomingAsync();

        await markInTransactionAsync(envelope);

        (await statusOfAsync(envelope)).ShouldBe(EnvelopeStatus.Handled);
    }

    [Fact]
    public async Task a_row_another_execution_already_handled_refuses_the_commit()
    {
        var envelope = await storeIncomingAsync();

        // The node that recovered the row while this one was frozen handled it first
        await _database.Inbox.MarkIncomingEnvelopeAsHandledAsync(envelope);
        await Task.Delay(5, TestContext.Current.CancellationToken);

        await Should.ThrowAsync<DuplicateIncomingEnvelopeException>(() => markInTransactionAsync(envelope));
    }

    [Fact]
    public async Task a_missing_row_does_not_refuse_the_commit()
    {
        var envelope = ObjectMother.Envelope();
        envelope.Status = EnvelopeStatus.Incoming;
        envelope.Destination = new Uri("local://gh5423");

        await markInTransactionAsync(envelope);
    }

    [Fact]
    public async Task an_eager_idempotency_row_written_by_this_execution_does_not_refuse_the_commit()
    {
        var envelope = await storeIncomingAsync();
        await _database.Inbox.MarkIncomingEnvelopeAsHandledAsync(envelope);

        // Eager idempotency flips the in-memory status when it writes its own Handled row
        envelope.Status = EnvelopeStatus.Handled;

        await markInTransactionAsync(envelope);
    }
}
