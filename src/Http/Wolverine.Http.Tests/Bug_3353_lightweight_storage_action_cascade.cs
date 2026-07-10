using Alba;
using IntegrationTests;
using JasperFx;
using JasperFx.CodeGeneration;
using JasperFx.Core.Reflection;
using Microsoft.AspNetCore.Builder;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Wolverine.EntityFrameworkCore;
using Wolverine.Http.Tests.EfCoreOnly;
using Wolverine.Persistence;
using Wolverine.Postgresql;
using Xunit;

namespace Wolverine.Http.Tests;

// Reproducer for GH-3353, the follow-up to GH-3291. The 3291 fix landed in
// EFCorePersistenceFrameProvider.ApplyTransactionSupport(chain, container) - the two-argument overload
// reached from the AutoApplyTransactions policy. There is a SECOND overload,
// ApplyTransactionSupport(chain, container, entityType), reached from the side-effect return types
// (IStorageAction<T>, Storage.Insert/Store/Update/Delete). That overload never enlists the endpoint's
// MessageContext in the outbox.
//
// The two overloads are mutually exclusive via the UsingEfCoreTransaction chain tag: whichever runs
// first wins. AutoApplyTransactions only fires when EFCorePersistenceFrameProvider.CanApply is true,
// and CanApply requires the chain to have a DbContext service dependency. So an HTTP endpoint that
// persists purely through a storage action - and never injects the DbContext - is claimed ONLY by the
// entityType overload. In Lightweight mode its cascaded messages are then dispatched by the send-now
// branch of MessageBus.PersistOrSendAsync BEFORE SaveChangesAsync commits: a downstream handler can
// observe the database before the row that triggered the message exists.
//
// Unlike the sibling GH-3291 reproducer (Bug_3291_lightweight_http_cascade_flushes_before_commit),
// this host registers EF Core but NOT Marten - the shape of the application the issue describes. That
// also means EF Core is naturally the only persistence frame provider, so the storage action resolves
// to the DbContext without any provider-ordering games. The endpoint and DbContext live in the tiny
// Wolverine.Http.Tests.EfCoreOnly assembly, and endpoint discovery is pinned there, because the main
// test assembly's endpoints assume Marten is registered and fail chain construction without it.
//
// Like the siblings, we assert at the codegen surface rather than at runtime: the runtime symptom (a
// message dispatched pre-commit) races the durability agent and the handler scheduler. The generated
// composition is the deterministic proof.
public class Bug_3353_lightweight_storage_action_cascade
{
    private static async Task<IAlbaHost> buildEfCoreOnlyLightweightHostAsync()
    {
        var builder = WebApplication.CreateBuilder();

        // Wolverine-integrated DbContext supplies the outbox enrollment for EF Core
        builder.Services.AddDbContextWithWolverineIntegration<Bug3353DbContext>(x =>
            x.UseNpgsql(Servers.PostgresConnectionString));

        builder.Host.UseWolverine(opts =>
        {
            // Pin endpoint discovery to the EF-Core-only assembly; see the class comment
            opts.ApplicationAssembly = typeof(Bug3353StorageActionEndpoint).Assembly;

            opts.Durability.Mode = DurabilityMode.Solo;

            // EF Core application WITHOUT Marten: the message store is the plain PostgreSQL-backed one
            opts.PersistMessagesWithPostgresql(Servers.PostgresConnectionString, "bug3353");

            // The bug is specific to Lightweight mode; Eager enrolls the DbContext in a transaction
            opts.UseEntityFrameworkCoreTransactions(TransactionMiddlewareMode.Lightweight);

            opts.Policies.AutoApplyTransactions();
            opts.Policies.UseDurableLocalQueues();

            opts.Discovery.DisableConventionalDiscovery();
            // The cascaded Bug3353ItemStored needs a routed handler so it actually buffers into Outstanding
            opts.Discovery.IncludeType<Bug3353ItemStoredHandler>();
        });

        builder.Services.AddWolverineHttp();

        return await AlbaHost.For(builder, app => app.MapWolverineEndpoints());
    }

    [Fact]
    public async Task storage_action_endpoint_enlists_outbox_and_does_not_flush_before_commit()
    {
        await using var host = await buildEfCoreOnlyLightweightHostAsync();

        var graph = host.Services.GetRequiredService<WolverineHttpOptions>().Endpoints!;
        var chain = graph.ChainFor("POST", "/bug3353/storage-action");
        chain.ShouldNotBeNull();

        chain.As<ICodeFile>().InitializeSynchronously(graph.Rules, graph, host.Services);
        var source = chain!.SourceCode;
        source.ShouldNotBeNull();

        // Guard: the storage action must be owned by EF Core for this test to mean anything
        source.ShouldContain("Bug3353DbContext");

        // (1) The MessageContext must be enlisted in the outbox, or the cascaded Bug3353ItemStored is
        // dispatched by the send-now branch of MessageBus.PersistOrSendAsync before SaveChangesAsync
        // commits.
        source.ShouldContain("EnlistInOutboxAsync");

        // (2) No standalone pre-commit flush.
        chain.Postprocessors.OfType<FlushOutgoingMessages>().ShouldBeEmpty(
            "A Lightweight-mode HTTP endpoint that persists via a storage action and cascades a message " +
            "must not flush its cascaded messages with a standalone FlushOutgoingMessages postprocessor - " +
            "that dispatches the cascade without any outbox protection.");

        // (3) Ordering: enlist -> SaveChangesAsync -> post-commit flush.
        // Match the actual call sites (".Method(") rather than bare names, which also appear in comments.
        var enlistAt = source.IndexOf(".EnlistInOutboxAsync(", StringComparison.Ordinal);
        var saveAt = source.IndexOf(".SaveChangesAsync(", StringComparison.Ordinal);
        var commitAt = source.IndexOf(".CommitAsync(", StringComparison.Ordinal);

        saveAt.ShouldBeGreaterThan(enlistAt, "SaveChangesAsync must run after the outbox enrollment");
        commitAt.ShouldBeGreaterThan(saveAt,
            "The outbox flush (EfCoreEnvelopeTransaction.CommitAsync) must run after SaveChangesAsync commits");
    }

    // Guard rail for the fix itself: EnlistDbContextInOutbox generates an EfCoreEnvelopeTransaction,
    // whose constructor throws unless the application has database-backed message persistence. An EF
    // Core application with NO message store (no outbox to protect in the first place) must keep its
    // pre-existing composition - SaveChangesAsync with send-now cascades - rather than gain generated
    // code that fails on every request.
    [Fact]
    public async Task storage_action_endpoint_without_message_persistence_is_left_alone()
    {
        var builder = WebApplication.CreateBuilder();

        builder.Services.AddDbContextWithWolverineIntegration<Bug3353DbContext>(x =>
            x.UseNpgsql(Servers.PostgresConnectionString));

        builder.Host.UseWolverine(opts =>
        {
            opts.ApplicationAssembly = typeof(Bug3353StorageActionEndpoint).Assembly;

            opts.Durability.Mode = DurabilityMode.Solo;

            // Deliberately NO PersistMessagesWithPostgresql / Marten / any message store
            opts.UseEntityFrameworkCoreTransactions(TransactionMiddlewareMode.Lightweight);

            opts.Policies.AutoApplyTransactions();

            opts.Discovery.DisableConventionalDiscovery();
            opts.Discovery.IncludeType<Bug3353ItemStoredHandler>();
        });

        builder.Services.AddWolverineHttp();

        await using var host = await AlbaHost.For(builder, app => app.MapWolverineEndpoints());

        var graph = host.Services.GetRequiredService<WolverineHttpOptions>().Endpoints!;
        var chain = graph.ChainFor("POST", "/bug3353/storage-action");
        chain.ShouldNotBeNull();

        chain.As<ICodeFile>().InitializeSynchronously(graph.Rules, graph, host.Services);
        var source = chain!.SourceCode;
        source.ShouldNotBeNull();

        source.ShouldContain("Bug3353DbContext");
        source.ShouldContain(".SaveChangesAsync(");
        source.ShouldNotContain("EnlistInOutboxAsync");
    }
}

// A routed handler so the cascaded Bug3353ItemStored has somewhere to go (durable local queue). Body
// is irrelevant - the test asserts the outbox enrollment/flush composition, not the handling.
public class Bug3353ItemStoredHandler
{
    public void Handle(Bug3353ItemStored _)
    {
    }
}
