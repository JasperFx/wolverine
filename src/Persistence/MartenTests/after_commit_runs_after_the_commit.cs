using IntegrationTests;
using JasperFx;
using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Model;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine;
using Wolverine.Attributes;
using Wolverine.Marten;
using Wolverine.Runtime.Handlers;
using Xunit;

namespace MartenTests;

/// <summary>
///     GH-3975. The per-provider half: an <c>AfterCommit</c> method must be emitted after Marten's own commit
///     frame, not merely after the handler.
/// </summary>
/// <remarks>
///     Asserting on the generated SOURCE rather than on list membership is the point. Membership only proves
///     the frame went into <c>PostCommitPostprocessors</c>; the guarantee being made is about where it lands
///     relative to a frame this provider contributes from a completely different code path
///     (<c>MartenPersistenceFrameProvider.ApplyTransactionSupport</c>), and only the emitted code shows that.
/// </remarks>
public class after_commit_runs_after_the_commit
{
    [Fact]
    public void the_after_commit_call_is_emitted_after_save_changes()
    {
        DynamicCodeBuilder.WithinCodegenCommand = true;
        try
        {
            using var host = Host.CreateDefaultBuilder()
                .UseWolverine(opts =>
                {
                    opts.Services.AddMarten(m =>
                    {
                        m.DisableNpgsqlLogging = true;
                        m.Connection(Servers.PostgresConnectionString);
                        m.DatabaseSchemaName = "after_commit_codegen";
                    }).IntegrateWithWolverine();

                    opts.Policies.AutoApplyTransactions();
                    opts.Discovery.DisableConventionalDiscovery()
                        .IncludeType(typeof(MartenAfterCommitHandler));
                })
                .Build();

            // Force HandlerGraph.Compile() without starting the host
            _ = host.Services.GetServices<ICodeFileCollection>().ToArray();

            var handlerGraph = host.Services.GetRequiredService<HandlerGraph>();
            var serviceVariableSource = host.Services.GetService<IServiceVariableSource>();
            var generatedAssembly = handlerGraph.StartAssembly(handlerGraph.Rules);

            var chain = handlerGraph.ChainFor(typeof(MartenAfterCommitMessage));
            chain.ShouldNotBeNull();

            ((ICodeFile)chain).AssembleTypes(generatedAssembly);
            var code = generatedAssembly.GenerateCode(serviceVariableSource);

            // Searching the emitted CALL form, not the bare name: the handler type is called
            // MartenAfterCommitHandler, so a bare "AfterCommit" search matches the type name first and
            // silently compares the wrong positions.
            var commit = code.IndexOf(".SaveChangesAsync(", StringComparison.Ordinal);
            var afterCommit = code.IndexOf($".{nameof(MartenAfterCommitHandler.AfterCommit)}(", StringComparison.Ordinal);

            commit.ShouldBeGreaterThan(-1, "Marten's commit frame was never emitted, so this test proves nothing");
            afterCommit.ShouldBeGreaterThan(-1, "the AfterCommit method was never emitted");

            afterCommit.ShouldBeGreaterThan(commit,
                "the AfterCommit call must be emitted AFTER Marten's SaveChangesAsync, or it observes a write that is not durable yet");
        }
        finally
        {
            DynamicCodeBuilder.WithinCodegenCommand = false;
        }
    }

    [Fact]
    public void an_after_method_still_runs_before_the_commit()
    {
        DynamicCodeBuilder.WithinCodegenCommand = true;
        try
        {
            using var host = Host.CreateDefaultBuilder()
                .UseWolverine(opts =>
                {
                    opts.Services.AddMarten(m =>
                    {
                        m.DisableNpgsqlLogging = true;
                        m.Connection(Servers.PostgresConnectionString);
                        m.DatabaseSchemaName = "after_commit_codegen";
                    }).IntegrateWithWolverine();

                    opts.Policies.AutoApplyTransactions();
                    opts.Discovery.DisableConventionalDiscovery()
                        .IncludeType(typeof(MartenAfterCommitHandler));
                })
                .Build();

            _ = host.Services.GetServices<ICodeFileCollection>().ToArray();

            var handlerGraph = host.Services.GetRequiredService<HandlerGraph>();
            var serviceVariableSource = host.Services.GetService<IServiceVariableSource>();
            var generatedAssembly = handlerGraph.StartAssembly(handlerGraph.Rules);

            var chain = handlerGraph.ChainFor(typeof(MartenAfterCommitMessage))!;
            ((ICodeFile)chain).AssembleTypes(generatedAssembly);
            var code = generatedAssembly.GenerateCode(serviceVariableSource);

            var commit = code.IndexOf(".SaveChangesAsync(", StringComparison.Ordinal);
            var after = code.IndexOf($".{nameof(MartenAfterCommitHandler.PostProcess)}(", StringComparison.Ordinal);

            after.ShouldBeGreaterThan(-1);

            // Compatibility guard. After's pre-commit position is long-standing behaviour and is deliberately
            // NOT changed by GH-3975 -- the new hook exists precisely because this position was the only one
            // available.
            after.ShouldBeLessThan(commit,
                "After methods must keep running BEFORE the commit; changing that would be a silent behaviour break");
        }
        finally
        {
            DynamicCodeBuilder.WithinCodegenCommand = false;
        }
    }
}

public record MartenAfterCommitMessage(Guid Id);

public class MartenAfterCommitDoc
{
    public Guid Id { get; set; }
}

[WolverineIgnore]
public static class MartenAfterCommitHandler
{
    // Takes IDocumentSession so MartenPersistenceFrameProvider.CanApply returns true and the commit
    // frame is actually added
    public static void Handle(MartenAfterCommitMessage message, IDocumentSession session)
    {
        session.Store(new MartenAfterCommitDoc { Id = message.Id });
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
