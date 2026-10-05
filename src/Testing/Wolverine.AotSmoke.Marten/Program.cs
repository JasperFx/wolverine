// AOT smoke test #6 (GH-4825) — see the csproj header for the full story. Boots a MARTEN-backed Wolverine.Http host
// inside a REAL Native AOT binary, then serves an endpoint that starts a Marten stream through IStartStream
// and one that returns Results<Ok<T>, ProblemHttpResult>. Exit 0 only when both answer and the stream exists.
//
// `codegen write` refreshes the committed pre-gen under Internal/Generated/ — run it under plain
// `dotnet run` from THIS directory, never from the native binary.
using System.Net;
using System.Text.Json.Serialization;
using JasperFx;
using JasperFx.CodeGeneration;
using Marten;
using Microsoft.AspNetCore.Http.HttpResults;
using Wolverine;
using Wolverine.Http;
using Wolverine.Marten;

var isCli = args.Length > 0 && args[0] is "codegen" or "describe" or "help" or "?";

var connectionString = Environment.GetEnvironmentVariable("AOT_SMOKE_POSTGRES")
                       ?? "Host=localhost;Port=5433;Database=postgres;Username=postgres;Password=postgres";

var builder = WebApplication.CreateSlimBuilder(args);

builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.TypeInfoResolverChain.Insert(0, AotMartenJsonContext.Default));

builder.Services.AddMarten(opts =>
    {
        opts.Connection(connectionString);
        // A schema per run, so a database left behind by a previous run cannot satisfy the assertions.
        opts.DatabaseSchemaName = $"aot_marten_{Guid.NewGuid():n}";
        opts.UseSystemTextJsonForSerialization(configure: o => o.TypeInfoResolverChain.Insert(0, AotMartenJsonContext.Default));
    })
    // Registers Envelope as a Marten document (failure 3).
    .IntegrateWithWolverine()
    .ApplyAllDatabaseChangesOnStartup();

builder.Host.UseWolverine(opts =>
{
    opts.ServiceName = "aot-marten-smoke";
    opts.Durability.Mode = DurabilityMode.Solo;
    opts.Discovery.DisableConventionalDiscovery();
    opts.Policies.AutoApplyTransactions();

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
builder.WebHost.UseUrls("http://127.0.0.1:0");

var app = builder.Build();

// Before the CLI branch, so `codegen write` sees the endpoints. Failures 1 and 2 throw from here.
app.MapWolverineEndpoints();

if (isCli)
{
    return await app.RunJasperFxCommands(args);
}

try
{
    await app.StartAsync();

    var address = app.Urls.FirstOrDefault();
    if (address == null)
    {
        await Console.Error.WriteLineAsync("FAIL: the host booted but reported no server address.");
        return 1;
    }

    using var client = new HttpClient();

    var id = Guid.NewGuid();
    var started = await client.PostAsync($"{address}/aot/orders/{id}", content: null);
    if (started.StatusCode != HttpStatusCode.Created)
    {
        await Console.Error.WriteLineAsync($"FAIL: the IStartStream endpoint answered {(int)started.StatusCode}, expected 201.");
        return 1;
    }

    var ok = await client.GetAsync($"{address}/aot/pings/{id}");
    if (ok.StatusCode != HttpStatusCode.OK)
    {
        await Console.Error.WriteLineAsync($"FAIL: the Results<Ok<T>, ProblemHttpResult> endpoint answered {(int)ok.StatusCode}, expected 200.");
        return 1;
    }

    // Vacuity guard: a 201 only proves the endpoint ran. The side effect must actually have started the stream.
    await using (var session = app.Services.GetRequiredService<IDocumentStore>().LightweightSession())
    {
        var events = await session.Events.FetchStreamAsync(id);
        if (events.Count != 1)
        {
            await Console.Error.WriteLineAsync($"FAIL: expected the endpoint's IStartStream to append 1 event, found {events.Count}.");
            return 1;
        }
    }

    await app.StopAsync();

    Console.WriteLine("OK: Native AOT Marten HTTP boot + IStartStream + Results<Ok<T>, ProblemHttpResult> smoke passed.");
    return 0;
}
catch (Exception e)
{
    await Console.Error.WriteLineAsync("FAIL: Native AOT Marten HTTP smoke crashed:");
    await Console.Error.WriteLineAsync(e.ToString());
    return 1;
}

public record AotOrderPlaced(Guid Id);

public class AotOrder
{
    public Guid Id { get; set; }
}

public record AotPing(Guid Id);

public static class AotMartenEndpoints
{
    // The reporting application's shape: (IResult, IStartStream?) from an HTTP endpoint (failure 1).
    [WolverinePost("/aot/orders/{id}")]
    public static (IResult, IStartStream?) PlaceOrder(Guid id)
        => (Results.Created($"/aot/orders/{id}", null), MartenOps.StartStream<AotOrder>(id, new AotOrderPlaced(id)));

    // IEndpointMetadataProvider return type (failure 2).
    [WolverineGet("/aot/pings/{id}")]
    public static Results<Ok<AotPing>, ProblemHttpResult> Ping(Guid id)
        => id == Guid.Empty ? TypedResults.Problem("empty id") : TypedResults.Ok(new AotPing(id));
}

[JsonSerializable(typeof(AotOrderPlaced))]
[JsonSerializable(typeof(AotPing))]
internal partial class AotMartenJsonContext : JsonSerializerContext;
