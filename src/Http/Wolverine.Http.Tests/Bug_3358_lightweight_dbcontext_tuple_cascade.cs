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

// Reproducer for GH-3358, the last of the three endpoint shapes in the GH-3291/GH-3353 defect family:
//
//   1. Endpoint injects IMessageBus and publishes explicitly     -> fixed by GH-3298 (RequiresOutbox true)
//   2. Endpoint persists via a storage action, no DbContext dep  -> fixed by GH-3357 (entityType overload)
//   3. Endpoint injects the DbContext and cascades via its tuple -> THIS
//
// Shape 3 is claimed by the two-argument ApplyTransactionSupport overload (the DbContext dependency
// makes CanApply true, so AutoApplyTransactions wins the chain), but that overload's GH-3291 branch was
// gated on chain.RequiresOutbox() - which HttpChain only satisfies for an injected IMessageBus /
// IMessageContext, never for tuple cascades. So the outbox enrollment was skipped and the cascaded
// message was dispatched by the send-now branch of MessageBus.PersistOrSendAsync BEFORE SaveChangesAsync
// committed. The fix drops the RequiresOutbox() gate, which GH-3357's message-database guard made safe:
// enlisting an endpoint that never cascades is a no-op flush at commit time, and persistence-less
// applications keep their send-now composition.
//
// Same conventions as the GH-3353 sibling: Marten-free EF Core host, endpoint pinned to the
// Wolverine.Http.Tests.EfCoreOnly assembly, assertions at the codegen surface because the runtime
// symptom races the durability agent.
public class Bug_3358_lightweight_dbcontext_tuple_cascade
{
    private static async Task<IAlbaHost> buildEfCoreOnlyLightweightHostAsync()
    {
        var builder = WebApplication.CreateBuilder();

        builder.Services.AddDbContextWithWolverineIntegration<Bug3353DbContext>(x =>
            x.UseNpgsql(Servers.PostgresConnectionString));

        builder.Host.UseWolverine(opts =>
        {
            opts.ApplicationAssembly = typeof(Bug3358DbContextCascadeEndpoint).Assembly;

            opts.Durability.Mode = DurabilityMode.Solo;

            opts.PersistMessagesWithPostgresql(Servers.PostgresConnectionString, "bug3353");

            // The bug is specific to Lightweight mode; Eager enrolls the DbContext in a transaction
            opts.UseEntityFrameworkCoreTransactions(TransactionMiddlewareMode.Lightweight);

            opts.Policies.AutoApplyTransactions();
            opts.Policies.UseDurableLocalQueues();

            opts.Discovery.DisableConventionalDiscovery();
            opts.Discovery.IncludeType<Bug3358ItemStoredHandler>();
        });

        builder.Services.AddWolverineHttp();

        return await AlbaHost.For(builder, app => app.MapWolverineEndpoints());
    }

    [Fact]
    public async Task dbcontext_injecting_tuple_cascade_endpoint_enlists_outbox_and_does_not_flush_before_commit()
    {
        await using var host = await buildEfCoreOnlyLightweightHostAsync();

        var graph = host.Services.GetRequiredService<WolverineHttpOptions>().Endpoints!;
        var chain = graph.ChainFor("POST", "/bug3358/dbcontext-cascade");
        chain.ShouldNotBeNull();

        // Guard: the endpoint must NOT satisfy RequiresOutbox - that is precisely the shape this test
        // pins. If it ever starts returning true here, shape 3 has collapsed into shape 1 and this
        // reproducer needs rethinking.
        chain.RequiresOutbox().ShouldBeFalse();

        chain.As<ICodeFile>().InitializeSynchronously(graph.Rules, graph, host.Services);
        var source = chain!.SourceCode;
        source.ShouldNotBeNull();

        // Guard: the chain is owned by the EF Core provider through the injected DbContext
        source.ShouldContain("Bug3353DbContext");

        // (1) The MessageContext must be enlisted in the outbox, or the cascaded Bug3358ItemStored is
        // dispatched by the send-now branch of MessageBus.PersistOrSendAsync before SaveChangesAsync
        // commits.
        source.ShouldContain("EnlistInOutboxAsync");

        // (2) No standalone pre-commit flush.
        chain.Postprocessors.OfType<FlushOutgoingMessages>().ShouldBeEmpty(
            "A Lightweight-mode HTTP endpoint that injects the DbContext and cascades through its return " +
            "tuple must not flush its cascaded messages with a standalone FlushOutgoingMessages " +
            "postprocessor - that dispatches the cascade without any outbox protection.");

        // (3) Ordering: enlist -> SaveChangesAsync -> post-commit flush.
        var enlistAt = source.IndexOf(".EnlistInOutboxAsync(", StringComparison.Ordinal);
        var saveAt = source.IndexOf(".SaveChangesAsync(", StringComparison.Ordinal);
        var commitAt = source.IndexOf(".CommitAsync(", StringComparison.Ordinal);

        saveAt.ShouldBeGreaterThan(enlistAt, "SaveChangesAsync must run after the outbox enrollment");
        commitAt.ShouldBeGreaterThan(saveAt,
            "The outbox flush (EfCoreEnvelopeTransaction.CommitAsync) must run after SaveChangesAsync commits");
    }

    // Guard rail: dropping the RequiresOutbox() gate must not regress persistence-less applications.
    // EnlistDbContextInOutbox generates an EfCoreEnvelopeTransaction, whose constructor throws unless
    // the application has database-backed message persistence - the hasDatabaseBackedMessagePersistence
    // guard from GH-3357 has to keep this host on plain SaveChangesAsync with send-now cascades.
    [Fact]
    public async Task dbcontext_injecting_endpoint_without_message_persistence_is_left_alone()
    {
        var builder = WebApplication.CreateBuilder();

        builder.Services.AddDbContextWithWolverineIntegration<Bug3353DbContext>(x =>
            x.UseNpgsql(Servers.PostgresConnectionString));

        builder.Host.UseWolverine(opts =>
        {
            opts.ApplicationAssembly = typeof(Bug3358DbContextCascadeEndpoint).Assembly;

            opts.Durability.Mode = DurabilityMode.Solo;

            // Deliberately NO PersistMessagesWithPostgresql / Marten / any message store
            opts.UseEntityFrameworkCoreTransactions(TransactionMiddlewareMode.Lightweight);

            opts.Policies.AutoApplyTransactions();

            opts.Discovery.DisableConventionalDiscovery();
            opts.Discovery.IncludeType<Bug3358ItemStoredHandler>();
        });

        builder.Services.AddWolverineHttp();

        await using var host = await AlbaHost.For(builder, app => app.MapWolverineEndpoints());

        var graph = host.Services.GetRequiredService<WolverineHttpOptions>().Endpoints!;
        var chain = graph.ChainFor("POST", "/bug3358/dbcontext-cascade");
        chain.ShouldNotBeNull();

        chain.As<ICodeFile>().InitializeSynchronously(graph.Rules, graph, host.Services);
        var source = chain!.SourceCode;
        source.ShouldNotBeNull();

        source.ShouldContain("Bug3353DbContext");
        source.ShouldContain(".SaveChangesAsync(");
        source.ShouldNotContain("EnlistInOutboxAsync");
    }
}

// A routed handler so the cascaded Bug3358ItemStored has somewhere to go (durable local queue). Body
// is irrelevant - the test asserts the outbox enrollment/flush composition, not the handling.
public class Bug3358ItemStoredHandler
{
    public void Handle(Bug3358ItemStored _)
    {
    }
}
