// AOT smoke test #7 (GH-4825) — see the csproj header for the full story. The FISHER twin of
// Wolverine.AotSmoke.Marten: the same three endpoint and handler shapes, against a SQLite file instead of
// a Postgres server. Exit 0 only when both endpoints answer, the stream was really started, and the
// handler's side effect really ran.
//
// `codegen write` refreshes the committed pre-gen under Internal/Generated/ — run it under plain
// `dotnet run` from THIS directory, never from the native binary.
using System.Net;
using System.Text.Json.Serialization;
using Fisher;
using JasperFx;
using JasperFx.CodeGeneration;
using Microsoft.AspNetCore.Http.HttpResults;
using Weasel.Core;
using Wolverine;
using Wolverine.Fisher;
using Wolverine.Http;

var isCli = args.Length > 0 && args[0] is "codegen" or "describe" or "help" or "?";

// A fresh file per run, for the reason Wolverine.AotSmoke.Saga spells out: a database left behind by a
// previous run could let the assertions below pass on stale rows.
var databasePath = Path.Combine(Path.GetTempPath(), $"wolverine_aot_fisher_{Guid.NewGuid():n}.db");

var builder = WebApplication.CreateSlimBuilder(args);

builder.Services.ConfigureHttpJsonOptions(o =>
    o.SerializerOptions.TypeInfoResolverChain.Insert(0, AotFisherJsonContext.Default));

builder.Services.AddFisher(opts =>
    {
        opts.Connection($"Data Source={databasePath}");
        opts.AutoCreateSchemaObjects = AutoCreate.All;

        // Reflection-based serialization is off in a native image, so the store needs a
        // source-generated resolver -- the GH-4805 lesson from the saga lane, through the document and
        // event serializer rather than the saga one.
        opts.ConfigureSerialization(configure: o =>
            o.TypeInfoResolverChain.Insert(0, AotFisherJsonContext.Default));

        // Fisher, not Wolverine: StorageFeatures closes a mapping builder over the document type with
        // CloseAndBuildAs, so a type whose mapping is first demanded at RUNTIME is missing native code.
        // Registering it here makes ILC generate the instantiation. Same note as the Marten lane.
        opts.Schema.For<AotFisherPing>();
    })
    .ApplyAllDatabaseChangesOnStartup()
    .IntegrateWithWolverine();

builder.Host.UseWolverine(opts =>
{
    opts.ServiceName = "aot-fisher-smoke";

    // Solo: one SQLite file takes one writer, and Fisher refuses HotCold anyway.
    opts.Durability.Mode = DurabilityMode.Solo;

    opts.Discovery.DisableConventionalDiscovery()
        .IncludeType(typeof(AotFisherHandler));

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

// Before the CLI branch, so `codegen write` sees the endpoints.
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
        await Console.Error.WriteLineAsync(
            $"FAIL: the IStartStream endpoint answered {(int)started.StatusCode}, expected 201.");
        return 1;
    }

    var ok = await client.GetAsync($"{address}/aot/pings/{id}");
    if (ok.StatusCode != HttpStatusCode.OK)
    {
        await Console.Error.WriteLineAsync(
            $"FAIL: the Results<Ok<T>, ProblemHttpResult> endpoint answered {(int)ok.StatusCode}, expected 200.");
        return 1;
    }

    var recorded = Guid.NewGuid();
    await app.Services.GetRequiredService<IMessageBus>().InvokeAsync(new RecordAotFisherPing(recorded));

    // Vacuity guard: a 201 only proves the endpoint ran. The side effects must actually have run -- the
    // Http lane booted clean having discovered ZERO endpoints before its own guard was added.
    await using (var session = app.Services.GetRequiredService<IDocumentStore>().LightweightSession())
    {
        var events = await session.Events.FetchStreamAsync(id);
        if (events.Count != 1)
        {
            await Console.Error.WriteLineAsync(
                $"FAIL: expected the endpoint's IStartStream to append 1 event, found {events.Count}.");
            return 1;
        }

        if (await session.LoadAsync<AotFisherPing>(recorded) == null)
        {
            await Console.Error.WriteLineAsync(
                "FAIL: the handler returned an IFisherOp but no AotFisherPing document was stored.");
            return 1;
        }
    }

    await app.StopAsync();

    Console.WriteLine(
        "OK: Native AOT Fisher HTTP boot + IStartStream + Results<Ok<T>, ProblemHttpResult> + handler IFisherOp smoke passed.");
    return 0;
}
catch (Exception e)
{
    await Console.Error.WriteLineAsync("FAIL: Native AOT Fisher HTTP smoke crashed:");
    await Console.Error.WriteLineAsync(e.ToString());
    return 1;
}
finally
{
    foreach (var path in Directory.EnumerateFiles(Path.GetDirectoryName(databasePath)!,
                 Path.GetFileName(databasePath) + "*"))
    {
        try
        {
            File.Delete(path);
        }
        catch
        {
            // A leftover temp file is not worth failing the lane over. SQLite's WAL and shm siblings are
            // why this globs rather than deleting one name.
        }
    }
}

public record AotFisherOrderPlaced(Guid Id);

public class AotFisherOrder
{
    public Guid Id { get; set; }
}

public record AotFisherPing(Guid Id);

public record RecordAotFisherPing(Guid Id);

public static class AotFisherHandler
{
    // Returns the IFisherOp INTERFACE, not the concrete op: the variable type is what
    // SideEffectPolicy.findMethod reflects over, and an interface is the shape whose Execute metadata the
    // trimmer drops.
    public static IFisherOp Handle(RecordAotFisherPing command)
    {
        return FisherOps.Store(new AotFisherPing(command.Id));
    }
}

public static class AotFisherEndpoints
{
    // (IResult, IStartStream?) from an HTTP endpoint -- the shape that threw
    // InvalidSideEffectException in a native image before GH-4825.
    [WolverinePost("/aot/orders/{id}")]
    public static (IResult, IStartStream?) PlaceOrder(Guid id)
    {
        return (Results.Created($"/aot/orders/{id}", null),
            FisherOps.StartStream<AotFisherOrder>(id, new AotFisherOrderPlaced(id)));
    }

    // IEndpointMetadataProvider return type: HttpChain closes its own Applier<T> over this.
    [WolverineGet("/aot/pings/{id}")]
    public static Results<Ok<AotFisherPing>, ProblemHttpResult> Ping(Guid id)
    {
        return id == Guid.Empty ? TypedResults.Problem("empty id") : TypedResults.Ok(new AotFisherPing(id));
    }
}

[JsonSerializable(typeof(AotFisherOrderPlaced))]
[JsonSerializable(typeof(AotFisherPing))]
[JsonSerializable(typeof(AotFisherOrder))]
internal partial class AotFisherJsonContext : JsonSerializerContext;
