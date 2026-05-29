using System.Runtime.CompilerServices;
using System.Text.Json;
using JasperFx;
using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Model;
using JasperFx.CodeGeneration.Services;
using Microsoft.Extensions.DependencyInjection;
using Wolverine.Http.FSharpContracts;

namespace Wolverine.Http.FSharpTests;

/// <summary>
///     Renders the Phase C Wolverine.Http endpoints (issue GH-2969) as F#. Builds each endpoint's
///     real <see cref="HttpChain" /> via <see cref="HttpChain.ChainFor{T}" /> (no web host needed),
///     assembles them into one <see cref="GeneratedAssembly" />, and emits the generated
///     <c>HttpHandler</c> adapters as F# via <see cref="GeneratedAssembly.GenerateFSharpCode" /> —
///     mirroring the C# preview path but swapping <c>GenerateCode</c> for <c>GenerateFSharpCode</c>.
/// </summary>
public static class HttpFSharpCodegenSample
{
    public static string GenerateCode()
    {
        // No-host container setup mirroring Wolverine.Http.Tests.initializing_endpoints_from_method_call.
        var registry = new ServiceCollection();
        registry.AddSingleton<JsonSerializerOptions>();

        var container = new ServiceContainer(registry, registry.BuildServiceProvider());
        var httpGraph = new HttpGraph(new WolverineOptions { ApplicationAssembly = typeof(ThingEndpoints).Assembly }, container);

        // Phase C increment 1 renders the static-response GET endpoint. The JSON POST endpoint
        // (ReadJsonBody / WriteJsonFrame) calls *instance* HttpHandler methods unqualified, which F#
        // cannot resolve from a `member _.Handle` body — blocked on the JasperFx F# self-identifier
        // gap (tracked upstream; same gap as RecordMessageCausationFrame). Add it back once JasperFx
        // emits a named self for generated members.
        var chains = new[]
        {
            HttpChain.ChainFor<ThingEndpoints>(x => x.Hello(), httpGraph)
        };

        var generatedAssembly = httpGraph.StartAssembly(httpGraph.Rules);
        foreach (var chain in chains)
        {
            ((ICodeFile)chain).AssembleTypes(generatedAssembly);
        }

        var serviceVariableSource = new ServiceCollectionServerVariableSource(container);
        return generatedAssembly.GenerateFSharpCode(serviceVariableSource);
    }

    public static string DefaultGeneratedFilePath([CallerFilePath] string thisFile = "")
    {
        var testProjectDir = Path.GetDirectoryName(thisFile)!;
        var srcTestingDir = Path.GetDirectoryName(testProjectDir)!;
        return Path.Combine(srcTestingDir, "Wolverine.Http.FSharpFixture", "Generated.fs");
    }

    public static string FixtureProjectPath([CallerFilePath] string thisFile = "")
    {
        var testProjectDir = Path.GetDirectoryName(thisFile)!;
        var srcTestingDir = Path.GetDirectoryName(testProjectDir)!;
        return Path.Combine(srcTestingDir, "Wolverine.Http.FSharpFixture", "Wolverine.Http.FSharpFixture.fsproj");
    }
}
