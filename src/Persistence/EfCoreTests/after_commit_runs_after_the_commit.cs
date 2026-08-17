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
using IntegrationTests;
using Microsoft.EntityFrameworkCore;
using Wolverine.EntityFrameworkCore;
using Wolverine.SqlServer;

namespace EfCoreTests;

/// <summary>
///     GH-3975. The per-provider half: an <c>AfterCommit</c> method must be emitted after EF Core's own
///     SaveChangesAsync, not merely after the handler.
/// </summary>
/// <remarks>
///     Asserting on the generated SOURCE rather than list membership: the guarantee is about where the
///     frame lands relative to the commit EFCorePersistenceFrameProvider contributes from a different code path.
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
                    opts.Services.AddDbContextWithWolverineIntegration<AfterCommitDbContext>(o =>
                    {
                        o.UseSqlServer(Servers.SqlServerConnectionString);
                    });

                    opts.PersistMessagesWithSqlServer(Servers.SqlServerConnectionString, "after_commit_codegen");
                    opts.UseEntityFrameworkCoreTransactions();

                    opts.Policies.AutoApplyTransactions();
                    opts.Discovery.DisableConventionalDiscovery().IncludeType(typeof(EfCoreAfterCommitHandler));
                })
                .Build();

            // Force HandlerGraph.Compile() without starting the host -- no live server is needed to
            // assert on frame ordering
            _ = host.Services.GetServices<ICodeFileCollection>().ToArray();

            var handlerGraph = host.Services.GetRequiredService<HandlerGraph>();
            var serviceVariableSource = host.Services.GetService<IServiceVariableSource>();
            var generatedAssembly = handlerGraph.StartAssembly(handlerGraph.Rules);

            var chain = handlerGraph.ChainFor(typeof(EfCoreAfterCommitMessage));
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
            "EF Core's SaveChangesAsync was never emitted, so this test proves nothing");
        afterCommit.ShouldBeGreaterThan(-1, "the AfterCommit method was never emitted");

        afterCommit.ShouldBeGreaterThan(commit,
            "the AfterCommit call must be emitted AFTER EF Core's SaveChangesAsync, or it observes a write that is not durable yet");
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

public record EfCoreAfterCommitMessage(Guid Id);

[WolverineIgnore]
public static class EfCoreAfterCommitHandler
{
    // Takes the DbContext so EFCorePersistenceFrameProvider.CanApply returns true and the commit
    // frame is actually added
    public static void Handle(EfCoreAfterCommitMessage message, AfterCommitDbContext db)
    {
        _ = db;
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

public class AfterCommitDbContext : DbContext
{
    public AfterCommitDbContext(DbContextOptions<AfterCommitDbContext> options) : base(options)
    {
    }

    public DbSet<AfterCommitRow> Rows => Set<AfterCommitRow>();
}

public class AfterCommitRow
{
    public Guid Id { get; set; }
}
