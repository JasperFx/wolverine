using IntegrationTests;
using Marten;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine;
using Wolverine.Marten;
using Wolverine.Tracking;

namespace MartenTests;

/// <summary>
///     GH-3911: a chain that <c>MartenOpPolicy</c> gave transaction support must also <b>report</b>
///     itself transactional.
/// </summary>
/// <remarks>
///     <para>
///     Same disagreement GH-3893 fixed for <c>[WriteAggregate]</c>: the policy ends the generated code in
///     <c>SaveChangesAsync</c>, so a chain answering <c>IsTransactional = false</c> is lying to every
///     <c>IChainPolicy</c> / <c>IHttpPolicy</c> keying on the flag.
///     </para>
///     <para>
///     It was left alone until now for a real reason — setting the flag from an <c>IChainPolicy</c> made
///     whether <c>EagerIdempotencyOnNonTransactionalChains</c> fired depend on the order the user called
///     <c>AutoApplyIdempotencyOnNonTransactionalHandlers()</c> relative to <c>IntegrateWithWolverine()</c>,
///     i.e. generated code varying by registration order. That policy is now always applied last, so
///     <b>both orders are asserted here</b>: the ordering guarantee is the thing that makes the flag safe,
///     and a regression in it would otherwise only show up as somebody's idempotency check quietly
///     appearing or disappearing.
///     </para>
/// </remarks>
public class marten_op_chains_are_transactional
{
    private static async Task<IHost> hostAsync(bool idempotencyFirst)
    {
        return await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Discovery.DisableConventionalDiscovery()
                    .IncludeType(typeof(StartTheStreamHandler));

                opts.Durability.Mode = DurabilityMode.Solo;

                if (idempotencyFirst)
                {
                    opts.Policies.AutoApplyIdempotencyOnNonTransactionalHandlers();
                }

                opts.Services.AddMarten(m =>
                {
                    m.Connection(Servers.PostgresConnectionString);
                    m.DatabaseSchemaName = "marten_op_transactional";
                }).IntegrateWithWolverine();

                if (!idempotencyFirst)
                {
                    opts.Policies.AutoApplyIdempotencyOnNonTransactionalHandlers();
                }
            }).StartAsync();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task a_marten_op_return_marks_the_chain_transactional(bool idempotencyFirst)
    {
        using var host = await hostAsync(idempotencyFirst);

        // Chains compile lazily
        await host.InvokeAsync(new StartTheStream(Guid.NewGuid()));

        var chain = host.GetRuntime().Handlers.ChainFor<StartTheStream>()!;

        chain.Postprocessors.OfType<JasperFx.CodeGeneration.Frames.MethodCall>()
            .Any(x => x.Method.Name == nameof(IDocumentSession.SaveChangesAsync))
            .ShouldBeTrue();

        chain.IsTransactional.ShouldBeTrue();
    }
}

public record StartTheStream(Guid Id);

public record StreamStarted(Guid Id);

public static class StartTheStreamHandler
{
    // A single IMartenOp return: an ISideEffect that MartenOpPolicy gives transaction support to
    public static IMartenOp Handle(StartTheStream command)
        => MartenOps.StartStream<OpStreamMarker>(command.Id, new StreamStarted(command.Id));
}

public class OpStreamMarker
{
    public Guid Id { get; set; }

    public void Apply(StreamStarted _)
    {
    }
}

/// <summary>
///     GH-3911: <c>[NonTransactional]</c> does not suppress the aggregate workflow's commit, and the
///     chain still reports itself transactional.
/// </summary>
/// <remarks>
///     Pinned rather than left to be rediscovered, because the surprising reading is the plausible one.
///     <c>[NonTransactional]</c> opts a chain out of <c>AutoApplyTransactions()</c> and nothing else —
///     <c>AutoApplyTransactions</c> is the only thing in Wolverine that reads it. The aggregate workflow
///     commits as part of its own contract: it loads a stream, hands you the state, and appends what you
///     return. Honouring the attribute there would silently discard those events.
/// </remarks>
public class non_transactional_does_not_suppress_the_aggregate_commit
{
    [Fact]
    public async Task the_aggregate_workflow_still_commits_and_still_reports_transactional()
    {
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Discovery.DisableConventionalDiscovery()
                    .IncludeType(typeof(NonTransactionalDepositHandler));

                opts.Durability.Mode = DurabilityMode.Solo;
                opts.Policies.AutoApplyTransactions();

                opts.Services.AddMarten(m =>
                {
                    m.Connection(Servers.PostgresConnectionString);
                    m.DatabaseSchemaName = "non_transactional_aggregate";
                }).IntegrateWithWolverine();
            }).StartAsync(TestContext.Current.CancellationToken);

        var streamId = Guid.NewGuid();
        await using (var session = host.DocumentStore().LightweightSession())
        {
            session.Events.StartStream<OpAccount>(streamId, new OpDeposited(10m));
            await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await host.InvokeAsync(new NonTransactionalDeposit(streamId, 5m));

        // The event was committed despite [NonTransactional]
        await using var verify = host.DocumentStore().LightweightSession();
        var account = await verify.Events.AggregateStreamAsync<OpAccount>(streamId,
            token: TestContext.Current.CancellationToken);
        account!.Balance.ShouldBe(15m);

        host.GetRuntime().Handlers.ChainFor<NonTransactionalDeposit>()!
            .IsTransactional.ShouldBeTrue();
    }
}

public record NonTransactionalDeposit(Guid OpAccountId, decimal Amount);

public record OpDeposited(decimal Amount);

public class OpAccount
{
    public Guid Id { get; set; }
    public decimal Balance { get; set; }

    public void Apply(OpDeposited e) => Balance += e.Amount;
}

public static class NonTransactionalDepositHandler
{
    [Wolverine.Attributes.NonTransactional]
    public static OpDeposited Handle(NonTransactionalDeposit command,
        [Wolverine.Persistence.EventSourcing.WriteModel] OpAccount account)
        => new(command.Amount);
}
