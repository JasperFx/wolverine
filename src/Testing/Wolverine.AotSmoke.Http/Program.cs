// AOT smoke test #4 (GH-4778) — see the csproj header for the full story. Boots Wolverine.Http inside a
// REAL Native AOT binary, through MapWolverineEndpoints, with an endpoint whose return type implements
// IResponseAware, then serves one request. Exit 0 only on boot + a 204 from that endpoint.
//
// `codegen write` refreshes the committed pre-gen under Internal/Generated/ — run it under plain
// `dotnet run`, never from the native binary.
using JasperFx;
using JasperFx.CodeGeneration;
using Wolverine;
using Wolverine.Configuration;
using Wolverine.Http;

var isCli = args.Length > 0 && args[0] is "codegen" or "describe" or "help" or "?";

// CreateSlimBuilder, not CreateBuilder: the AOT-oriented host builder, which is what an application
// publishing Native AOT would actually use.
var builder = WebApplication.CreateSlimBuilder(args);

builder.Host.UseWolverine(opts =>
{
    opts.ServiceName = "aot-http-smoke";
    opts.Durability.Mode = DurabilityMode.Solo;
    opts.Discovery.DisableConventionalDiscovery();

    if (!isCli)
    {
        opts.CodeGeneration.TypeLoadMode = TypeLoadMode.Static;
        opts.Services.CritterStackDefaults(cr =>
        {
            cr.Production.AssertAllPreGeneratedTypesExist = true;
            cr.Development.AssertAllPreGeneratedTypesExist = true;
        });
    }
});

builder.Services.AddWolverineHttp();

// Port 0: an ephemeral port, so the lane cannot collide with anything else on a CI runner.
builder.WebHost.UseUrls("http://127.0.0.1:0");

var app = builder.Build();

// Called BEFORE the CLI branch, and unconditionally. `codegen write` only emits the HTTP endpoint
// registry and the endpoint code files if the endpoints have been discovered by the time the command
// runs; returning early here produced a committed pre-gen tree with a handler registry and no HTTP in it
// at all, which would have made this smoke pass while proving nothing about the HTTP path.
//
// This is also the call that threw in GH-4778: DiscoverEndpoints -> HttpChain..ctor ->
// applyAttributesAndConfigureMethods -> tryApplyResponseAware -> CloseAndBuildAs.
app.MapWolverineEndpoints();

if (isCli)
{
    return await app.RunJasperFxCommands(args);
}

try
{
    await app.StartAsync();

    if (!AotHttpResponseMarker.Configured)
    {
        await Console.Error.WriteLineAsync(
            "FAIL: the host booted but IResponseAware.ConfigureResponse never ran, so Applier<T> was never closed and this smoke would pass vacuously.");
        return 1;
    }

    var address = app.Urls.FirstOrDefault();
    if (address == null)
    {
        await Console.Error.WriteLineAsync("FAIL: the host booted but reported no server address.");
        return 1;
    }

    using var client = new HttpClient();
    var response = await client.PostAsync($"{address}/aot/response-aware", content: null);

    await app.StopAsync();

    // 204, not 200: an IResponseAware declares that it owns its own response, and this one deliberately
    // writes nothing, so Wolverine has no resource to write and says so. The status is asserted only to
    // prove the endpoint is really routed and served in the native image -- the load-bearing assertion is
    // Configured above, which is what proves Applier<T> was closed and invoked.
    if (response.StatusCode != System.Net.HttpStatusCode.NoContent)
    {
        await Console.Error.WriteLineAsync(
            $"FAIL: the endpoint answered {(int)response.StatusCode}, expected 204.");
        return 1;
    }

    Console.WriteLine("OK: Native AOT HTTP boot + response-aware endpoint smoke passed.");
    return 0;
}
catch (Exception e)
{
    await Console.Error.WriteLineAsync("FAIL: Native AOT HTTP smoke crashed:");
    await Console.Error.WriteLineAsync(e.ToString());
    return 1;
}

/// <summary>
///     Stands in for Marten's <c>UpdatedAggregate</c>, which is what the GH-4778 reporter's endpoints
///     returned: an <see cref="IResponseAware" /> returned from an HTTP endpoint, with no store behind it,
///     so this smoke needs only Wolverine.Http.
/// </summary>
public class AotHttpResponseMarker : IResponseAware
{
    public static bool Configured;

    /// <summary>
    ///     Records that it ran, and deliberately does NOT rewrite the chain's response.
    /// </summary>
    /// <remarks>
    ///     What this smoke pins is that <c>Applier&lt;AotHttpResponseMarker&gt;</c> could be closed and
    ///     constructed at startup at all, which is the GH-4778 crash; the hook's own body is immaterial to
    ///     that, because the failure happens in CloseAndBuildAs before Apply() is reached. A version that
    ///     did call <c>chain.UseForResponse</c> generated a write of the WRONG variable
    ///     (<c>WriteString(httpContext, aotHttpResponseMarker, 404)</c> while ResourceType had become
    ///     string), which looks like a real defect in response rewriting for a plain MethodCall but is a
    ///     separate question from rooting, and not one to smuggle into this lane.
    /// </remarks>
    public static void ConfigureResponse(IChain chain) => Configured = true;
}

public static class AotHttpResponseAwareEndpoint
{
    [WolverinePost("/aot/response-aware")]
    public static AotHttpResponseMarker Post() => new();
}
