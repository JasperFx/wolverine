using System.Runtime.CompilerServices;
using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Model;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Wolverine;
using Wolverine.EntityFrameworkCore;
using Wolverine.Runtime.Handlers;
using WolverineFSharpSample;

namespace Wolverine.EfCore.FSharpTests;

/// <summary>
///     Renders the sample's EF Core handler chain (issue GH-2969) as F#. Builds a minimal in-memory
///     host that discovers <see cref="CreateItemHandler" /> with EF Core transactional middleware
///     enabled, compiles the handler graph without starting it, and emits the adapter as F# via
///     <see cref="GeneratedAssembly.GenerateFSharpCode" /> — exercising the EF Core transactional
///     frames (enroll-in-outbox, SaveChanges, commit).
/// </summary>
public static class EfCoreFSharpCodegenSample
{
    public static string GenerateCode()
    {
        DynamicCodeBuilder.WithinCodegenCommand = true;
        try
        {
            using var host = Host.CreateDefaultBuilder()
                .UseWolverine(opts =>
                {
                    opts.Services.AddDbContextWithWolverineIntegration<ItemsDbContext>(o =>
                        o.UseInMemoryDatabase("items").ConfigureWarnings(w =>
                            w.Ignore(InMemoryEventId.TransactionIgnoredWarning)));

                    opts.UseEntityFrameworkCoreTransactions();
                    opts.Policies.AutoApplyTransactions();

                    opts.Discovery.DisableConventionalDiscovery();
                    opts.Discovery.IncludeType<CreateItemHandler>();
                })
                .Build();

            // Force HandlerGraph.Compile() without starting the host (no Roslyn, no real DB).
            _ = host.Services.GetServices<ICodeFileCollection>().ToArray();

            var handlerGraph = host.Services.GetRequiredService<HandlerGraph>();
            var chain = handlerGraph.ChainFor(typeof(CreateItemCommand))
                        ?? throw new InvalidOperationException("No handler chain was built for CreateItemCommand.");

            var serviceVariableSource = host.Services.GetService<IServiceVariableSource>();
            var generatedAssembly = handlerGraph.StartAssembly(handlerGraph.Rules);
            ((ICodeFile)chain).AssembleTypes(generatedAssembly);

            return generatedAssembly.GenerateFSharpCode(serviceVariableSource);
        }
        finally
        {
            DynamicCodeBuilder.WithinCodegenCommand = false;
        }
    }

    public static string DefaultGeneratedFilePath([CallerFilePath] string thisFile = "")
    {
        var testProjectDir = Path.GetDirectoryName(thisFile)!;
        var srcTestingDir = Path.GetDirectoryName(testProjectDir)!;
        return Path.Combine(srcTestingDir, "Wolverine.EfCore.FSharpFixture", "Generated.fs");
    }

    public static string FixtureProjectPath([CallerFilePath] string thisFile = "")
    {
        var testProjectDir = Path.GetDirectoryName(thisFile)!;
        var srcTestingDir = Path.GetDirectoryName(testProjectDir)!;
        return Path.Combine(srcTestingDir, "Wolverine.EfCore.FSharpFixture", "Wolverine.EfCore.FSharpFixture.fsproj");
    }
}
