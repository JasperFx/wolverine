using JasperFx;
using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Model;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine;
using Wolverine.Attributes;
using Wolverine.Runtime.Handlers;
using Xunit;
using Microsoft.Azure.Cosmos;
using Wolverine.CosmosDb;

namespace CosmosDbTests;

/// <summary>
///     GH-3975. The per-provider half: an <c>AfterCommit</c> method must be emitted after CosmosDb's own
///     outbox flush, not merely after the handler.
/// </summary>
/// <remarks>
///     Note CosmosDb has NO commit postprocessor: CosmosDbPersistenceFrameProvider.ApplyTransactionSupport adds a
///     TransactionalFrame to the MIDDLEWARE (the outbox enlistment) and only FlushOutgoingMessages to the
///     postprocessors. So the meaningful assertion here is that the after-commit call lands after that flush --
///     i.e. after everything the provider contributes to the tail of the chain -- rather than after a
///     SaveChangesAsync that this provider never emits.
/// </remarks>
public class after_commit_runs_after_the_commit
{
    private static string generateCode()
    {
        DynamicCodeBuilder.WithinCodegenCommand = true;
        try
        {
            using var host = Host.CreateDefaultBuilder()
                .UseWolverine(opts =>
                {
                    // Dummy emulator connection string -- CosmosClient is lazy, no connection is made
                    // during codegen. Same approach as transactional_frame_code_generation.
                    opts.Services.AddSingleton(_ => new CosmosClient(
                        "AccountEndpoint=https://localhost:8081/;AccountKey=C2y6yDjf5/R+ob0N8A7Cgv30VRDJIWEHLM+4QDU5DE2nQ9nDuVTqobD4b8mGGyPMbIZnqyMsEcaGQy67XIw/Jw=="));
                    opts.UseCosmosDbPersistence("wolverine_after_commit_codegen");

                    opts.Policies.AutoApplyTransactions();
                    opts.Discovery.DisableConventionalDiscovery().IncludeType(typeof(CosmosAfterCommitHandler));
                })
                .Build();

            // Force HandlerGraph.Compile() without starting the host -- no live server is needed to
            // assert on frame ordering
            _ = host.Services.GetServices<ICodeFileCollection>().ToArray();

            var handlerGraph = host.Services.GetRequiredService<HandlerGraph>();
            var serviceVariableSource = host.Services.GetService<IServiceVariableSource>();
            var generatedAssembly = handlerGraph.StartAssembly(handlerGraph.Rules);

            var chain = handlerGraph.ChainFor(typeof(CosmosAfterCommitMessage));
            chain.ShouldNotBeNull();

            ((ICodeFile)chain).AssembleTypes(generatedAssembly);
            return generatedAssembly.GenerateCode(serviceVariableSource);
        }
        finally
        {
            DynamicCodeBuilder.WithinCodegenCommand = false;
        }
    }

    [Fact]
    public void the_after_commit_call_is_emitted_after_the_commit()
    {
        var code = generateCode();

        // Searching the emitted CALL form, not the bare name: the handler type name itself contains
        // "AfterCommit", so a bare search matches the type name first and silently compares the wrong
        // positions.
        var commit = code.IndexOf("FlushOutgoingMessagesAsync", StringComparison.Ordinal);
        var afterCommit = code.IndexOf(".AfterCommit(", StringComparison.Ordinal);

        commit.ShouldBeGreaterThan(-1,
            "CosmosDb's outbox flush was never emitted, so this test proves nothing");
        afterCommit.ShouldBeGreaterThan(-1, "the AfterCommit method was never emitted");

        afterCommit.ShouldBeGreaterThan(commit,
            "the AfterCommit call must be emitted AFTER CosmosDb's outbox flush, or it observes a write that is not durable yet");
    }

    [Fact]
    public void an_after_method_still_runs_before_the_commit()
    {
        var code = generateCode();

        var commit = code.IndexOf("FlushOutgoingMessagesAsync", StringComparison.Ordinal);
        var after = code.IndexOf(".PostProcess(", StringComparison.Ordinal);

        after.ShouldBeGreaterThan(-1);

        // Compatibility guard. After's pre-commit position is long-standing and deliberately NOT changed
        // by GH-3975 -- the new hook exists precisely because this position was the only one available.
        after.ShouldBeLessThan(commit,
            "After methods must keep running BEFORE the commit; changing that would be a silent behaviour break");
    }
}

public record CosmosAfterCommitMessage(Guid Id);

[WolverineIgnore]
public static class CosmosAfterCommitHandler
{
    // Takes Container so CosmosDbPersistenceFrameProvider.CanApply returns true
    public static void Handle(CosmosAfterCommitMessage message, Container container)
    {
        _ = container;
    }

    // "PostProcess" is one of the After convention names, and unlike "After" it is not a substring of
    // "AfterCommit" -- which matters because these assertions are string index comparisons
    public static void PostProcess()
    {
    }

    public static void AfterCommit()
    {
    }
}
