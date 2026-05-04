using System.Diagnostics;
using IntegrationTests;
using JasperFx;
using JasperFx.Core;
using JasperFx.Core.Reflection;
using JasperFx.Resources;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using SharedPersistenceModels.Items;
using Shouldly;
using Wolverine;
using Wolverine.EntityFrameworkCore;
using Wolverine.Logging;
using Wolverine.Persistence;
using Wolverine.Persistence.Durability;
using Wolverine.Postgresql;
using Wolverine.Runtime.Handlers;
using Wolverine.Tracking;
using Xunit;
using Xunit.Abstractions;

namespace EfCoreTests.Bugs;

/// <summary>
/// Companion to the HTTP-side reproducer
/// (<c>Wolverine.Http.Tests/Bug_efcore_outbox_flush_before_commit.cs</c>) for the EF
/// Core outbox flush-before-commit bug surfaced via the sample at
/// https://github.com/dmytro-pryvedeniuk/outbox.
///
/// On the HTTP side the bug manifests because <c>HttpChain.ShouldFlushOutgoingMessages</c>
/// returns true, which combines with <see cref="Wolverine.Configuration.IChain.RequiresOutbox"/>
/// to add a <see cref="Wolverine.Persistence.FlushOutgoingMessages"/> postprocessor in
/// <see cref="Wolverine.EntityFrameworkCore.Codegen.EFCorePersistenceFrameProvider.ApplyTransactionSupport"/>.
/// In Eager mode that postprocessor runs BEFORE <c>EnrollDbContextInTransaction</c>'s
/// wrapping <c>efCoreEnvelopeTransaction.CommitAsync(...)</c> and breaks outbox ordering
/// — the outgoing message is sent through the transport sender before the EF Core
/// transaction (which holds the wolverine_outgoing row) commits.
///
/// On the message-handler side the bug doesn't currently manifest because
/// <c>HandlerChain.ShouldFlushOutgoingMessages</c> returns false — the second condition
/// short-circuits the postprocessor add. Tests in this class cover both transaction
/// modes (Eager + Lightweight) on the handler side, both for codegen shape (the
/// FlushOutgoingMessages postprocessor must NOT be added on handlers regardless of
/// mode) and for the round-trip cleanup invariant (the wolverine_outgoing row is
/// removed after the durable destination consumes the cascaded message).
///
/// (<see cref="Wolverine.EntityFrameworkCore.Internals.EfCoreEnvelopeTransaction.CommitAsync"/>
/// already flushes after commit in Eager mode; in Lightweight mode the message
/// pipeline's natural end-of-handler flush takes over after SaveChangesAsync commits.
/// Either way, no separate FlushOutgoingMessages postprocessor is needed on handler
/// chains.)
/// </summary>
[Collection("postgresql")]
public class Bug_efcore_outbox_flush_before_commit
{
    private readonly ITestOutputHelper _output;

    public Bug_efcore_outbox_flush_before_commit(ITestOutputHelper output)
    {
        _output = output;
    }

    private static Task<IHost> buildHostAsync(string schema, TransactionMiddlewareMode mode, bool useDurableLocalQueues)
    {
        return Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Services.AddDbContextWithWolverineIntegration<ItemsDbContext>(o =>
                {
                    o.UseNpgsql(Servers.PostgresConnectionString);
                });

                opts.PersistMessagesWithPostgresql(Servers.PostgresConnectionString, schema);
                opts.UseEntityFrameworkCoreTransactions(mode);
                opts.Policies.AutoApplyTransactions();

                if (useDurableLocalQueues)
                {
                    // Promotes the local queue receiving OutboxBugItemCreated to a
                    // DurableLocalQueue. That makes envelope.Sender.IsDurable=true, which
                    // is the gate that causes Envelope.PersistAsync to write the
                    // wolverine_outgoing row in the first place — without it the cleanup
                    // assertions are vacuous.
                    opts.Policies.UseDurableLocalQueues();
                }

                opts.UseEntityFrameworkCoreWolverineManagedMigrations();
                opts.Services.AddResourceSetupOnStartup(StartupAction.ResetState);
            })
            .StartAsync();
    }

    [Theory]
    [InlineData(TransactionMiddlewareMode.Eager, "outbox_flush_handler_codegen_eager")]
    [InlineData(TransactionMiddlewareMode.Lightweight, "outbox_flush_handler_codegen_light")]
    public async Task handler_chain_does_not_flush_outgoing_messages_before_efcore_commit(
        TransactionMiddlewareMode mode, string schema)
    {
        using var host = await buildHostAsync(schema, mode, useDurableLocalQueues: false);

        var chain = host.Services
            .GetRequiredService<HandlerGraph>()
            .HandlerFor<CreateOutboxBugItem>()!
            .As<MessageHandler>()!
            .Chain!;

        // Direct postprocessor inspection — doesn't depend on dynamic vs. static
        // codegen mode. In Eager mode EnrollDbContextInTransaction.CommitAsync is the
        // sole legitimate flush trigger; in Lightweight mode the message pipeline's
        // natural end-of-handler flush takes over after SaveChangesAsync commits.
        // Either way no standalone FlushOutgoingMessages postprocessor should appear.
        // Today this is enforced by HandlerChain.ShouldFlushOutgoingMessages returning
        // false; the test is regression coverage so a future change there can't
        // silently introduce a double flush (Eager) or an unsafe early flush.
        chain.Postprocessors.OfType<FlushOutgoingMessages>().ShouldBeEmpty(
            $"EFCorePersistenceFrameProvider added a FlushOutgoingMessages postprocessor on a handler chain in {mode} mode.");
    }

    /// <summary>
    /// Locks down the round-trip cleanup invariant for the EF Core transactional
    /// middleware: a handler that publishes a cascading message destined for a durable
    /// endpoint must end with the wolverine_outgoing_envelopes row deleted. The path is:
    ///   - <c>EfCoreEnvelopeTransaction.PersistOutgoingAsync</c> adds an
    ///     <c>OutgoingMessage</c> entity to the DbContext when the cascading message is
    ///     published.
    ///   - <c>SaveChangesAsync</c> postprocessor writes the row to
    ///     wolverine_outgoing_envelopes inside the open EF Core transaction (Eager) or
    ///     in EF Core's implicit per-call transaction (Lightweight).
    ///   - In Eager mode <c>EnrollDbContextInTransaction.CommitAsync</c> commits then
    ///     calls <c>FlushOutgoingMessagesAsync</c>; in Lightweight mode the message
    ///     pipeline flushes naturally after the handler returns. Either way the durable
    ///     sender (<c>DurableLocalQueue</c> here, but the same path applies to
    ///     broker-backed durable senders via <c>DurableSendingAgent</c>) processes the
    ///     envelope and removes the outgoing row via
    ///     <c>IMessageOutbox.DeleteOutgoingAsync</c>.
    ///
    /// Run for both transaction modes per <c>UseEntityFrameworkCoreTransactions</c>'s
    /// supported settings — without explicit coverage of both, a fix targeted at one
    /// mode could silently regress the other.
    /// </summary>
    [Theory]
    [InlineData(TransactionMiddlewareMode.Eager, "outbox_cleanup_eager")]
    [InlineData(TransactionMiddlewareMode.Lightweight, "outbox_cleanup_light")]
    public async Task outgoing_row_is_deleted_after_send_to_durable_local_queue_completes(
        TransactionMiddlewareMode mode, string schema)
    {
        using var host = await buildHostAsync(schema, mode, useDurableLocalQueues: true);
        var store = host.Services.GetRequiredService<IMessageStore>();

        // Sanity check on the starting state — ResetState should have left the outgoing
        // table empty, but be explicit so a misconfiguration doesn't make the post-test
        // assertion accidentally pass.
        var beforeCounts = await store.Admin.FetchCountsAsync();
        beforeCounts.Outgoing.ShouldBe(0);

        // TrackActivity waits for cascaded sends + downstream receivers to finish, so
        // by the time it returns the durable sender has had its chance to process the
        // envelope and delete the outgoing row.
        await host
            .TrackActivity()
            .Timeout(30.Seconds())
            .IncludeExternalTransports()
            .SendMessageAndWaitAsync(new CreateOutboxBugItem(Guid.NewGuid(), $"Joe Mixon ({mode})"));

        // The cleanup is performed via a separate Npgsql connection inside
        // DurableSendingAgent's RetryBlock, so a brief poll covers the case where the
        // delete batch hasn't drained yet — without making the failure flake on a
        // one-off slow CI tick.
        var afterCounts = await pollOutgoingCountAsync(store, expected: 0);
        _output.WriteLine($"[{mode}] final wolverine_outgoing count: {afterCounts.Outgoing}");
        afterCounts.Outgoing.ShouldBe(0,
            customMessage: $"wolverine_outgoing_envelopes still has rows after the durable destination consumed the message in {mode} mode. The post-send DeleteOutgoingAsync path didn't run, the EF Core commit ordering is wrong, or the durable sender isn't acking. The durability agent would eventually re-send these stranded rows, breaking exactly-once.");
    }

    private static async Task<PersistedCounts> pollOutgoingCountAsync(IMessageStore store, int expected)
    {
        var sw = Stopwatch.StartNew();
        PersistedCounts counts;
        do
        {
            counts = await store.Admin.FetchCountsAsync();
            if (counts.Outgoing <= expected) return counts;
            await Task.Delay(100);
        } while (sw.Elapsed < TimeSpan.FromSeconds(5));

        return counts;
    }
}

public record CreateOutboxBugItem(Guid Id, string Name);

public record OutboxBugItemCreated(Guid Id);

public class CreateOutboxBugItemHandler
{
    // Mirrors the sample in https://github.com/dmytro-pryvedeniuk/outbox: a handler that
    // takes a DbContext (triggering the EF Core transaction middleware) and publishes a
    // cascading message (engaging the FlushOutgoingMessages-postprocessor wiring at
    // EFCorePersistenceFrameProvider.cs:202-207).
    public OutboxBugItemCreated Handle(CreateOutboxBugItem command, ItemsDbContext db)
    {
        db.Items.Add(new Item { Id = command.Id, Name = command.Name });
        return new OutboxBugItemCreated(command.Id);
    }
}

public class OutboxBugItemCreatedHandler
{
    // No-op consumer so default local routing has somewhere to deliver the cascaded
    // event. The cleanup test asserts the outgoing row is gone after this handler runs.
    public void Handle(OutboxBugItemCreated _) { }
}
