using JasperFx;
using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Model;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Raven.Client.Documents;
using Raven.Client.Documents.Session;
using Shouldly;
using Wolverine;
using Wolverine.Attributes;
using Wolverine.RavenDb;
using Wolverine.Runtime.Handlers;
using Xunit;

namespace RavenDbTests;

/// <summary>
///     GH-3975. The per-provider half: an <c>AfterCommit</c> method must be emitted after RavenDb's own
///     <c>SaveChangesAsync</c> commit frame, not merely after the handler.
/// </summary>
/// <remarks>
///     No embedded server here on purpose. RavenDB's <c>DocumentStore.Initialize()</c> does not open a
///     connection — the request executor is lazy — so a store pointed at a URL nothing is listening on is
///     enough to compile the chain, and frame ordering is a pure codegen property. That keeps this assertion
///     fast and independent of the <c>DatabaseFixture</c> the behavioural RavenDb tests need.
/// </remarks>
public class after_commit_runs_after_the_commit
{
    private static string generateCode()
    {
        DynamicCodeBuilder.WithinCodegenCommand = true;

        using var store = new DocumentStore
        {
            Urls = ["http://localhost:8080"],
            Database = "wolverine_after_commit_codegen"
        };
        store.Initialize();

        try
        {
            using var host = Host.CreateDefaultBuilder()
                .UseWolverine(opts =>
                {
                    opts.Services.AddSingleton<IDocumentStore>(store);
                    opts.UseRavenDbPersistence();
                    opts.Durability.Mode = DurabilityMode.Solo;

                    opts.Policies.AutoApplyTransactions();
                    opts.Discovery.DisableConventionalDiscovery()
                        .IncludeType(typeof(RavenAfterCommitHandler));
                })
                .Build();

            // Force HandlerGraph.Compile() without starting the host
            _ = host.Services.GetServices<ICodeFileCollection>().ToArray();

            var handlerGraph = host.Services.GetRequiredService<HandlerGraph>();
            var serviceVariableSource = host.Services.GetService<IServiceVariableSource>();
            var generatedAssembly = handlerGraph.StartAssembly(handlerGraph.Rules);

            var chain = handlerGraph.ChainFor(typeof(RavenAfterCommitMessage));
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
        var commit = code.IndexOf(".SaveChangesAsync(", StringComparison.Ordinal);
        var afterCommit = code.IndexOf(".AfterCommit(", StringComparison.Ordinal);

        commit.ShouldBeGreaterThan(-1,
            "RavenDb's SaveChangesAsync was never emitted, so this test proves nothing");
        afterCommit.ShouldBeGreaterThan(-1, "the AfterCommit method was never emitted");

        afterCommit.ShouldBeGreaterThan(commit,
            "the AfterCommit call must be emitted AFTER RavenDb's SaveChangesAsync, or it observes a write that is not durable yet");
    }

    [Fact]
    public void an_after_method_still_runs_before_the_commit()
    {
        var code = generateCode();

        var commit = code.IndexOf(".SaveChangesAsync(", StringComparison.Ordinal);
        var after = code.IndexOf(".PostProcess(", StringComparison.Ordinal);

        after.ShouldBeGreaterThan(-1);

        // Compatibility guard. After's pre-commit position is long-standing and deliberately NOT changed
        // by GH-3975 -- the new hook exists precisely because this position was the only one available.
        after.ShouldBeLessThan(commit,
            "After methods must keep running BEFORE the commit; changing that would be a silent behaviour break");
    }
}

public record RavenAfterCommitMessage(Guid Id);

[WolverineIgnore]
public static class RavenAfterCommitHandler
{
    // Takes IAsyncDocumentSession so RavenDbPersistenceFrameProvider.CanApply returns true and the
    // commit frame is actually added
    public static void Handle(RavenAfterCommitMessage message, IAsyncDocumentSession session)
    {
        _ = session;
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
