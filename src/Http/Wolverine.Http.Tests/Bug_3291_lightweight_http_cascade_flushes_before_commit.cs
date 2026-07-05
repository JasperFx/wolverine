using Alba;
using IntegrationTests;
using JasperFx;
using JasperFx.CodeGeneration;
using JasperFx.Core.Reflection;
using Marten;
using Microsoft.AspNetCore.Builder;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Wolverine.EntityFrameworkCore;
using Wolverine.Marten;
using Wolverine.Persistence;
using WolverineWebApi;
using Xunit;

namespace Wolverine.Http.Tests;

// Reproducer for GH-3291. A Wolverine.Http endpoint has no incoming envelope, so (unlike a message
// handler, whose MessageContext is enlisted in the outbox by ReadEnvelope at runtime) its
// MessageContext.Transaction stays null. In TransactionMiddlewareMode.Lightweight the EF Core
// transaction middleware does NOT enroll the endpoint's DbContext/context in the outbox, so
// MessageBus.PersistOrSendAsync takes the StoreAndForwardAsync() (send-now) branch and the cascaded
// message is sent BEFORE the SaveChangesAsync postprocessor commits - silently dropping the
// transactional-outbox guarantee the HTTP docs advertise. Message handlers are unaffected in both
// modes; the bug is specific to HTTP endpoints in Lightweight mode.
//
// Like the sibling Eager-mode reproducer (Bug_efcore_outbox_flush_before_commit), we assert at the
// codegen surface rather than at runtime: the runtime symptom (a stranded wolverine_outgoing row) is
// cleaned up by the durability agent within ~250ms and races any post-request query. The generated
// composition is the deterministic proof.
//
// Pre-fix state (the bug): the Lightweight HTTP chain carries a standalone FlushOutgoingMessages
// postprocessor as its ONLY flush trigger, and its MessageContext is never enlisted, so the generated
// code never calls EnlistInOutboxAsync. Fixed state: the chain enlists the DbContext in the outbox
// WITHOUT an explicit BeginTransactionAsync (an IFlushesMessages middleware -> no standalone
// FlushOutgoingMessages), and the buffered messages are flushed after the commit.
public class Bug_3291_lightweight_http_cascade_flushes_before_commit
{
    private static async Task<IAlbaHost> buildLightweightHostAsync()
    {
        var schema = "ef_lw_" + Guid.NewGuid().ToString("N")[..8];
        var builder = WebApplication.CreateBuilder();

        // Wolverine-integrated DbContext supplies the outbox enrollment for EF Core (mirrors the
        // AddDbContextWithWolverineIntegration setup the issue reports).
        builder.Services.AddDbContextWithWolverineIntegration<ItemsDbContext>(x =>
            x.UseNpgsql(Servers.PostgresConnectionString));

        builder.Host.UseWolverine(opts =>
        {
            opts.Durability.Mode = DurabilityMode.Solo;

            opts.Services.AddMarten(m =>
            {
                m.Connection(Servers.PostgresConnectionString);
                m.DatabaseSchemaName = schema;
            }).IntegrateWithWolverine();

            // The bug is specific to Lightweight mode. Eager already works (see the sibling reproducer).
            opts.UseEntityFrameworkCoreTransactions(TransactionMiddlewareMode.Lightweight);

            opts.Policies.AutoApplyTransactions();
            opts.Policies.UseDurableLocalQueues();

            opts.Discovery.DisableConventionalDiscovery();
            // The cascaded ItemCreated needs a routed handler so it actually buffers into Outstanding.
            opts.Discovery.IncludeType<LightweightCascadeItemCreatedHandler>();
            opts.Discovery.IncludeAssembly(typeof(Bug_3291_lightweight_http_cascade_flushes_before_commit).Assembly);
        });

        builder.Services.AddWolverineHttp();

        return await AlbaHost.For(builder, app => app.MapWolverineEndpoints());
    }

    [Fact]
    public async Task lightweight_http_endpoint_enlists_outbox_and_does_not_flush_before_commit()
    {
        await using var host = await buildLightweightHostAsync();

        var graph = host.Services.GetRequiredService<WolverineHttpOptions>().Endpoints!;
        var chain = graph.ChainFor("POST", "/ef/lightweight/publish");
        chain.ShouldNotBeNull();

        // (1) No standalone pre-commit flush. Pre-fix, applyEagerCommitOrLightweightFlush adds a
        // FlushOutgoingMessages postprocessor that (because the context is never enlisted) runs the
        // send BEFORE SaveChangesAsync commits. Fixed, the enlist middleware is IFlushesMessages, so
        // this standalone flush is gone and the commit path does the post-commit flush instead.
        chain.Postprocessors.OfType<FlushOutgoingMessages>().ShouldBeEmpty(
            "GH-3291: a Lightweight-mode HTTP endpoint that cascades messages still has a standalone " +
            "FlushOutgoingMessages postprocessor. Its MessageContext is never enlisted in the outbox, " +
            "so the cascade is sent before SaveChangesAsync commits.");

        // (2) The generated code must enlist the DbContext + IMessageContext in the outbox so the
        // cascade buffers and flushes after commit. Pre-fix this call is absent.
        // GH-3291: pre-fix the Lightweight HTTP endpoint never enrolls its DbContext/context in the
        // outbox, so cascaded messages are sent immediately instead of buffered until after the commit.
        chain.As<ICodeFile>().InitializeSynchronously(graph.Rules, graph, host.Services);
        var source = chain!.SourceCode;
        source.ShouldNotBeNull();
        source.ShouldContain("EnlistInOutboxAsync");

        // (3) Ordering: enroll in the outbox -> [endpoint body] -> SaveChangesAsync commits -> the
        // envelope transaction's CommitAsync flushes the buffered messages. The flush must come AFTER
        // the commit; that is the whole point of the fix.
        // Match the actual call sites (".Method(") rather than bare names, which also appear in comments.
        var enlistAt = source.IndexOf(".EnlistInOutboxAsync(", StringComparison.Ordinal);
        var saveAt = source.IndexOf(".SaveChangesAsync(", StringComparison.Ordinal);
        var commitAt = source.IndexOf(".CommitAsync(", StringComparison.Ordinal);

        saveAt.ShouldBeGreaterThan(enlistAt, "SaveChangesAsync must run after the outbox enrollment");
        commitAt.ShouldBeGreaterThan(saveAt,
            "The outbox flush (EfCoreEnvelopeTransaction.CommitAsync) must run after SaveChangesAsync commits");
    }
}

public static class LightweightEfCascadeEndpoint
{
    // Writes an entity AND cascades a message: the exact shape from the GH-3291 report. In Lightweight
    // mode the cascade must not be sent until SaveChangesAsync commits the item.
    [WolverinePost("/ef/lightweight/publish")]
    public static async Task Publish(CreateItemCommand command, ItemsDbContext db, IMessageBus bus)
    {
        var item = new Item { Name = command.Name };
        db.Items.Add(item);
        await bus.PublishAsync(new ItemCreated { Id = item.Id });
    }
}

// A routed handler so the cascaded ItemCreated has somewhere to go (durable local queue). Body is
// irrelevant — the test asserts the outbox enrollment/flush composition, not the handling.
public class LightweightCascadeItemCreatedHandler
{
    public void Handle(ItemCreated _)
    {
    }
}
