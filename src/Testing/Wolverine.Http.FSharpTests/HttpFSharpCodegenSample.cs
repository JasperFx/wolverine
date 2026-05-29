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

        // GET (static string response) + POST (JSON body bind + JSON response). The JSON path calls
        // the inherited instance HttpHandler methods ReadJsonAsync/WriteJsonAsync, now qualified with
        // the generated member's `this` self identifier (JasperFx 2.2.4 / jasperfx#393).
        var chains = new[]
        {
            HttpChain.ChainFor<ThingEndpoints>(x => x.Hello(), httpGraph),
            HttpChain.ChainFor<ThingEndpoints>(x => x.Create(null!), httpGraph),
            HttpChain.ChainFor<ThingEndpoints>(x => x.GetById(null!), httpGraph),
            HttpChain.ChainFor<ThingEndpoints>(x => x.Search(null!), httpGraph),
            HttpChain.ChainFor<ThingEndpoints>(x => x.GetItems(null!, 0), httpGraph), // typed int route value
            HttpChain.ChainFor<ThingEndpoints>(x => x.Paged(0), httpGraph)            // typed int query value
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
