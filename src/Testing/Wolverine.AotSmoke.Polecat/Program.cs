// AOT smoke test #8 (GH-4825) — see the csproj header for the full story. The POLECAT twin of
// Wolverine.AotSmoke.Marten: the same three endpoint and handler shapes, against SQL Server 2025 instead
// of Postgres. Exit 0 only when both endpoints answer, the stream was really started, and the handler's
// side effect really ran.
//
// `codegen write` refreshes the committed pre-gen under Internal/Generated/ — run it under plain
// `dotnet run` from THIS directory, never from the native binary.
using System.Net;
using System.Text.Json.Serialization;
using IntegrationTests;
using JasperFx;
using JasperFx.CodeGeneration;
using Microsoft.AspNetCore.Http.HttpResults;
using Polecat;
using Wolverine;
using Wolverine.Http;
using Wolverine.Polecat;

var isCli = args.Length > 0 && args[0] is "codegen" or "describe" or "help" or "?";

var builder = WebApplication.CreateSlimBuilder(args);

builder.Services.ConfigureHttpJsonOptions(o =>
    o.SerializerOptions.TypeInfoResolverChain.Insert(0, AotPolecatJsonContext.Default));

builder.Services.AddPolecat(opts =>
    {
        // Servers, not a literal: it knows this repo's SQL Server is on 1434 and honours the
        // WOLVERINE_SQLSERVER override each parallelized CI worker lane sets.
        opts.ConnectionString = Servers.SqlServerConnectionString;

        // A schema per run, so a database left behind by a previous run cannot satisfy the assertions.
        opts.DatabaseSchemaName = $"aot_polecat_{Guid.NewGuid():n}";

        // Reflection-based serialization is off in a native image, so the store needs a
        // source-generated resolver -- the GH-4805 lesson from the saga lane, through the document and
        // event serializer rather than the saga one.
        opts.ConfigureSerialization(configure: o =>
            o.TypeInfoResolverChain.Insert(0, AotPolecatJsonContext.Default));

        // Polecat, not Wolverine: StorageFeatures closes a mapping builder over the document type with
        // CloseAndBuildAs, so a type whose mapping is first demanded at RUNTIME is missing native code.
        // Registering it here makes ILC generate the instantiation. Same note as the Marten lane.
        opts.Schema.For<AotPolecatPing>();
    })
    .IntegrateWithWolverine()
    .ApplyAllDatabaseChangesOnStartup();

builder.Host.UseWolverine(opts =>
{
    opts.ServiceName = "aot-polecat-smoke";
    opts.Durability.Mode = DurabilityMode.Solo;

    opts.Discovery.DisableConventionalDiscovery()
        .IncludeType(typeof(AotPolecatHandler));

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
    await app.Services.GetRequiredService<IMessageBus>().InvokeAsync(new RecordAotPolecatPing(recorded));

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

        if (await session.LoadAsync<AotPolecatPing>(recorded) == null)
        {
            await Console.Error.WriteLineAsync(
                "FAIL: the handler returned an IPolecatOp but no AotPolecatPing document was stored.");
            return 1;
        }
    }

    await app.StopAsync();

    Console.WriteLine(
        "OK: Native AOT Polecat HTTP boot + IStartStream + Results<Ok<T>, ProblemHttpResult> + handler IPolecatOp smoke passed.");
    return 0;
}
catch (Exception e)
{
    await Console.Error.WriteLineAsync("FAIL: Native AOT Polecat HTTP smoke crashed:");
    await Console.Error.WriteLineAsync(e.ToString());
    return 1;
}

public record AotPolecatOrderPlaced(Guid Id);

public class AotPolecatOrder
{
    public Guid Id { get; set; }
}

public record AotPolecatPing(Guid Id);

public record RecordAotPolecatPing(Guid Id);

public static class AotPolecatHandler
{
    // Returns the IPolecatOp INTERFACE, not the concrete op: the variable type is what
    // SideEffectPolicy.findMethod reflects over, and an interface is the shape whose Execute metadata the
    // trimmer drops.
    public static IPolecatOp Handle(RecordAotPolecatPing command)
    {
        return PolecatOps.Store(new AotPolecatPing(command.Id));
    }
}

public static class AotPolecatEndpoints
{
    // (IResult, IStartStream?) from an HTTP endpoint -- the shape that threw
    // InvalidSideEffectException in a native image before GH-4825.
    [WolverinePost("/aot/orders/{id}")]
    public static (IResult, IStartStream?) PlaceOrder(Guid id)
    {
        return (Results.Created($"/aot/orders/{id}", null),
            PolecatOps.StartStream<AotPolecatOrder>(id, new AotPolecatOrderPlaced(id)));
    }

    // IEndpointMetadataProvider return type: HttpChain closes its own Applier<T> over this.
    [WolverineGet("/aot/pings/{id}")]
    public static Results<Ok<AotPolecatPing>, ProblemHttpResult> Ping(Guid id)
    {
        return id == Guid.Empty ? TypedResults.Problem("empty id") : TypedResults.Ok(new AotPolecatPing(id));
    }
}

[JsonSerializable(typeof(AotPolecatOrderPlaced))]
[JsonSerializable(typeof(AotPolecatPing))]
[JsonSerializable(typeof(AotPolecatOrder))]
internal partial class AotPolecatJsonContext : JsonSerializerContext;
