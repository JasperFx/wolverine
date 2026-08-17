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
using Fisher;
using JasperFx.Resources;
using Wolverine.Fisher;

namespace FisherTests;

/// <summary>
///     GH-3975. The per-provider half: an <c>AfterCommit</c> method must be emitted after Fisher's own
///     commit frame, not merely after the handler.
/// </summary>
/// <remarks>
///     Asserting on the generated SOURCE rather than on list membership is the point. Membership only proves
///     the frame went into <c>PostCommitPostprocessors</c>; the guarantee is about where it lands relative to a
///     frame this provider contributes from a completely different code path
///     (<c>FisherPersistenceFrameProvider.ApplyTransactionSupport</c>), and only the emitted code shows that.
/// </remarks>
public class after_commit_runs_after_the_commit
{
    private static string generateCodeFor(Type handlerType, Type messageType)
    {
        DynamicCodeBuilder.WithinCodegenCommand = true;
        try
        {
            using var host = Host.CreateDefaultBuilder()
                .UseWolverine(opts =>
                {
                    opts.Durability.Mode = DurabilityMode.Solo;
                    opts.Services.AddFisher(o =>
                    {
                        // Codegen only -- the file is never opened
                        o.Connection($"Data Source={Path.Combine(Path.GetTempPath(), "wolverine_after_commit_codegen.db")}");
                        o.AutoCreateSchemaObjects = AutoCreate.None;
                    }).IntegrateWithWolverine();

                    opts.Policies.AutoApplyTransactions();
                    opts.Discovery.DisableConventionalDiscovery().IncludeType(handlerType);
                })
                .Build();

            // Force HandlerGraph.Compile() without starting the host -- no live server is needed to
            // assert on frame ordering
            _ = host.Services.GetServices<ICodeFileCollection>().ToArray();

            var handlerGraph = host.Services.GetRequiredService<HandlerGraph>();
            var serviceVariableSource = host.Services.GetService<IServiceVariableSource>();
            var generatedAssembly = handlerGraph.StartAssembly(handlerGraph.Rules);

            var chain = handlerGraph.ChainFor(messageType);
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
        var code = generateCodeFor(typeof(FisherAfterCommitHandler), typeof(FisherAfterCommitMessage));

        // Searching the emitted CALL form, not the bare name: the handler type name itself contains
        // "AfterCommit", so a bare search matches the type name first and silently compares the wrong
        // positions.
        var commit = code.IndexOf(".SaveChangesAsync(", StringComparison.Ordinal);
        var afterCommit = code.IndexOf(".AfterCommit(", StringComparison.Ordinal);

        commit.ShouldBeGreaterThan(-1,
            "Fisher's commit frame was never emitted, so this test proves nothing");
        afterCommit.ShouldBeGreaterThan(-1, "the AfterCommit method was never emitted");

        afterCommit.ShouldBeGreaterThan(commit,
            "the AfterCommit call must be emitted AFTER Fisher's commit frame, or it observes a write that is not durable yet");
    }

    [Fact]
    public void an_after_method_still_runs_before_the_commit()
    {
        var code = generateCodeFor(typeof(FisherAfterCommitHandler), typeof(FisherAfterCommitMessage));

        var commit = code.IndexOf(".SaveChangesAsync(", StringComparison.Ordinal);
        var after = code.IndexOf(".PostProcess(", StringComparison.Ordinal);

        after.ShouldBeGreaterThan(-1);

        // Compatibility guard. After's pre-commit position is long-standing behaviour and is deliberately
        // NOT changed by GH-3975 -- the new hook exists precisely because this position was the only one
        // available.
        after.ShouldBeLessThan(commit,
            "After methods must keep running BEFORE the commit; changing that would be a silent behaviour break");
    }
}

public record FisherAfterCommitMessage(Guid Id);

[WolverineIgnore]
public static class FisherAfterCommitHandler
{
    // Takes IDocumentSession so FisherPersistenceFrameProvider.CanApply returns true and the commit
    // frame is actually added
    public static void Handle(FisherAfterCommitMessage message, IDocumentSession session)
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
