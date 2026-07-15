using System.Runtime.CompilerServices;
using System.Text.Json;
using JasperFx;
using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Model;
using JasperFx.CodeGeneration.Services;
using Microsoft.Extensions.DependencyInjection;
using Wolverine.Http.CodeGen;
using Wolverine.Http.FSharpContracts;
using Wolverine.Http.Runtime.MultiTenancy;
using Wolverine.Runtime;

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
        // IWolverineRuntime is needed as a constructor parameter for chains that use IMessageBus.
        // Register a placeholder so the ServiceCollectionServerVariableSource can resolve the type;
        // the F# fixture only compiles the generated code, it never executes it.
        registry.AddSingleton<IWolverineRuntime>(_ => null!);

        var container = new ServiceContainer(registry, registry.BuildServiceProvider());
        var httpGraph = new HttpGraph(new WolverineOptions { ApplicationAssembly = typeof(ThingEndpoints).Assembly }, container);

        // GET (static string response) + POST (JSON body bind + JSON response). The JSON path calls
        // the inherited instance HttpHandler methods ReadJsonAsync/WriteJsonAsync, now qualified with
        // the generated member's `this` self identifier (JasperFx 2.2.4 / jasperfx#393).
        var helloChain    = HttpChain.ChainFor<ThingEndpoints>(x => x.Hello(), httpGraph);
        var createChain   = HttpChain.ChainFor<ThingEndpoints>(x => x.Create(null!), httpGraph);
        var getByIdChain  = HttpChain.ChainFor<ThingEndpoints>(x => x.GetById(null!), httpGraph);
        var searchChain   = HttpChain.ChainFor<ThingEndpoints>(x => x.Search(null!), httpGraph);
        var getItemsChain = HttpChain.ChainFor<ThingEndpoints>(x => x.GetItems(null!, 0), httpGraph);
        var pagedChain    = HttpChain.ChainFor<ThingEndpoints>(x => x.Paged(0), httpGraph);
        var resultChain   = HttpChain.ChainFor<ThingEndpoints>(x => x.GetResult(null!), httpGraph);

        // WriteEmptyBodyStatusCode.GenerateFSharpCode — void-returning endpoint yields 204.
        var deleteChain  = HttpChain.ChainFor<ThingEndpoints>(x => x.Delete(null!), httpGraph);

        // UseMessageBusFrame + CreateMessageContextWithMaybeTenantFrame.GenerateFSharpCode —
        // the IMessageBus parameter causes a MessageContext to be constructed in the handler.
        var publishChain = HttpChain.ChainFor<ThingEndpoints>(x => x.Publish(null!, null!), httpGraph);

        // QueryStringBindingFrame.GenerateFSharpCode — [FromQuery] complex type is bound from
        // individual query-string variables and passed to the record constructor.
        var filterChain  = HttpChain.ChainFor<ThingEndpoints>(x => x.Filter(null!), httpGraph);

        // MaybeEndWithResultFrame.GenerateFSharpCode — a static auth-check call whose IResult
        // return is wrapped in MaybeEndWithResultFrame to short-circuit when the check fails.
        var authedChain  = HttpChain.ChainFor<AuthedEndpoints>(x => x.Get(), httpGraph);
        var checkCall    = new MethodCall(typeof(AuthHelpers), nameof(AuthHelpers.CheckAuth));
        var maybeEnd     = new MaybeEndWithResultFrame(checkCall.ReturnVariable!);
        authedChain.Middleware.Add(checkCall);
        authedChain.Middleware.Add(maybeEnd);

        var chains = new[]
        {
            helloChain, createChain, getByIdChain, searchChain, getItemsChain,
            pagedChain, resultChain, deleteChain, publishChain, filterChain, authedChain
        };

        // TagHttpHandlerFrame.GenerateFSharpCode — applied to all chains via TagHttpHandlerPolicy.
        // DetectTenantIdFrame.GenerateFSharpCode — applied to all chains when at least one
        // tenant-detection strategy is configured.
        var tagPolicy = new TagHttpHandlerPolicy();
        tagPolicy.Apply(chains, httpGraph.Rules, container);

        var tenantDetection = new TenantIdDetection();
        tenantDetection.IsRequestHeaderValue("x-tenant-id");
        ((IHttpPolicy)tenantDetection).Apply(chains, httpGraph.Rules, container);

        var generatedAssembly = httpGraph.StartAssembly(httpGraph.Rules);
        foreach (var chain in chains)
        {
            ((ICodeFile)chain).AssembleTypes(generatedAssembly);
        }

        // WriteEndpointTypesFrame.GenerateFSharpCode — emitted by HttpEndpointRegistryCodeFile as
        // the body of GeneratedHttpEndpointRegistry.EndpointTypes().
        var allEndpointTypes = new[] { typeof(ThingEndpoints), typeof(AuthedEndpoints) };
        var registryFile = new HttpEndpointRegistryCodeFile(allEndpointTypes);
        ((ICodeFile)registryFile).AssembleTypes(generatedAssembly);

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
