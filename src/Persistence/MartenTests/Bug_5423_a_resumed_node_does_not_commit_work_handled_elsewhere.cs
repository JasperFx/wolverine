using IntegrationTests;
using JasperFx.Resources;
using Marten;
using Microsoft.Extensions.Hosting;
using Npgsql;
using Shouldly;
using Wolverine;
using Wolverine.Marten;
using Wolverine.Tracking;

namespace MartenTests;

/// <summary>
/// GH-5423. A node paused past StaleNodeTimeout is ejected, another node recovers its Incoming inbox rows and handles
/// them, and when the paused node resumes it finishes the envelopes it still holds in memory. Its commit used to go
/// through -- the mark-as-handled matched id and received_at only -- so the work was committed twice. The commit is
/// now refused when another execution has already marked the row Handled, and the envelope is discarded as a
/// duplicate.
/// </summary>
public class Bug_5423_a_resumed_node_does_not_commit_work_handled_elsewhere : IAsyncLifetime
{
    private const string Schema = "fence_5423";
    private IHost _host = null!;

    public async ValueTask InitializeAsync()
    {
        _host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Discovery.DisableConventionalDiscovery()
                    .IncludeType(typeof(HandledElsewhereHandler))
                    .IncludeType(typeof(HandledHereHandler));
                opts.Durability.Mode = DurabilityMode.Solo;
                opts.Policies.AutoApplyTransactions();

                // So the envelope is in the inbox before the handler runs, as it is for a durable listener
                opts.Policies.UseDurableLocalQueues();

                opts.Services.AddMarten(m =>
                {
                    m.DisableNpgsqlLogging = true;
                    m.Connection(Servers.PostgresConnectionString);
                    m.DatabaseSchemaName = Schema;
                }).IntegrateWithWolverine();
            }).StartAsync(TestContext.Current.CancellationToken);

        await _host.ResetResourceState();
    }

    public async ValueTask DisposeAsync()
    {
        await _host.StopAsync();
        _host.Dispose();
    }

    [Fact]
    public async Task work_another_node_already_handled_is_not_committed_again()
    {
        var id = Guid.NewGuid();
        var tracked = await _host.TrackActivity()
            .DoNotAssertOnExceptionsDetected()
            .SendMessageAndWaitAsync(new HandledElsewhere(id));

        tracked.Discarded.SingleMessage<HandledElsewhere>().Id.ShouldBe(id);

        await using var session = _host.DocumentStore().QuerySession();
        (await session.LoadAsync<FenceRecord>(id, TestContext.Current.CancellationToken)).ShouldBeNull();
    }

    [Fact]
    public async Task ordinary_work_still_commits()
    {
        var id = Guid.NewGuid();
        await _host.TrackActivity().SendMessageAndWaitAsync(new HandledHere(id));

        await using var session = _host.DocumentStore().QuerySession();
        (await session.LoadAsync<FenceRecord>(id, TestContext.Current.CancellationToken)).ShouldNotBeNull();
    }

    /// <summary>
    /// What the node that recovered the row did while this one was frozen: handled the message and committed
    /// </summary>
    internal static async Task MarkHandledByAnotherNodeAsync(Envelope envelope)
    {
        await using var conn = new NpgsqlConnection(Servers.PostgresConnectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            $"update {Schema}.wolverine_incoming_envelopes set status = 'Handled', owner_id = 0, keep_until = now() + interval '1 day' where id = @id";
        cmd.Parameters.AddWithValue("id", envelope.Id);
        (await cmd.ExecuteNonQueryAsync()).ShouldBe(1);
    }
}

public record HandledElsewhere(Guid Id);

public record HandledHere(Guid Id);

public class FenceRecord
{
    public Guid Id { get; set; }
}

public static class HandledElsewhereHandler
{
    public static async Task Handle(HandledElsewhere message, IDocumentSession session, Envelope envelope)
    {
        await Bug_5423_a_resumed_node_does_not_commit_work_handled_elsewhere.MarkHandledByAnotherNodeAsync(envelope);
        session.Store(new FenceRecord { Id = message.Id });
    }
}

public static class HandledHereHandler
{
    public static void Handle(HandledHere message, IDocumentSession session)
    {
        session.Store(new FenceRecord { Id = message.Id });
    }
}
