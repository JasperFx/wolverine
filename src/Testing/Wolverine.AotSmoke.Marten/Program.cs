// AOT smoke test #6 (GH-4825) — see the csproj header for the full story. Boots a MARTEN-backed Wolverine.Http host
// inside a REAL Native AOT binary, then serves an endpoint that starts a Marten stream through IStartStream
// and one that returns Results<Ok<T>, ProblemHttpResult>. Exit 0 only when both answer and the stream exists.
//
// `codegen write` refreshes the committed pre-gen under Internal/Generated/ — run it under plain
// `dotnet run` from THIS directory, never from the native binary.
using System.Net;
using System.Text.Json.Serialization;
using IntegrationTests;
using JasperFx;
using JasperFx.CodeGeneration;
using Marten;
using Microsoft.AspNetCore.Http.HttpResults;
using Wolverine;
using Wolverine.Http;
using Wolverine.Marten;
using Wolverine.Persistence;

var isCli = args.Length > 0 && args[0] is "codegen" or "describe" or "help" or "?";

// Servers, not a literal: it is the one place that knows this repo's docker-compose Postgres is on 5433
// rather than Marten's 5432, and it honours the WOLVERINE_POSTGRES override each parallelized CI worker
// lane sets. The file is dependency-free, so it links into a PublishAot project unchanged.
var connectionString = Servers.PostgresConnectionString;

var builder = WebApplication.CreateSlimBuilder(args);

builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.TypeInfoResolverChain.Insert(0, AotMartenJsonContext.Default));

builder.Services.AddMarten(opts =>
    {
        opts.Connection(connectionString);
        // A schema per run, so a database left behind by a previous run cannot satisfy the assertions.
        opts.DatabaseSchemaName = $"aot_marten_{Guid.NewGuid():n}";
        opts.UseSystemTextJsonForSerialization(configure: o => o.TypeInfoResolverChain.Insert(0, AotMartenJsonContext.Default));

        // Marten, not Wolverine: StorageFeatures.Build closes DocumentMappingBuilder<T> with
        // CloseAndBuildAs, so a document type whose mapping is first demanded at RUNTIME throws
        // NotSupportedException ("missing native code or metadata") in a native image. Registering it
        // here makes ILC generate the instantiation. Left explicit, with this note, because the handler
        // below stores an AotPing and the failure it would otherwise hit has nothing to do with the
        // rooting this lane is testing.
        opts.Schema.For<AotPing>();
    })
    // Registers Envelope as a Marten document (failure 3).
    .IntegrateWithWolverine()
    .ApplyAllDatabaseChangesOnStartup();

builder.Host.UseWolverine(opts =>
{
    opts.ServiceName = "aot-marten-smoke";

    // GH-4843. BALANCED, deliberately, and the only native lane that is. Every store-backed lane ran Solo,
    // and in Solo the agent messaging path is never used: agents start locally and the leader's assignment
    // messages never route. That is why the fourteen unrooted MessageRouter<T> closes over the framework's
    // own agent messages (GH-4825 item 4, fixed in #4842) were found by a user and not by this repository.
    // A single Balanced node still runs NodeAgentController, still elects itself leader, still evaluates
    // assignments and still routes the assignment messages -- it just has nothing to hand off to. The
    // leadership and assignment waits after StartAsync are what make that path load-bearing rather than
    // incidental. One measured limit, so nobody over-reads this lane: with those fourteen factories
    // stripped out it still boots and passes, on net9.0 and net10.0 alike. It covers the clustered path
    // in a native image; it does not reproduce that particular failure.
    opts.Durability.Mode = DurabilityMode.Balanced;
    // GH-4825. The MESSAGE-handler half of the side-effect failure. SideEffectPolicy is applied to handler
    // chains and HTTP chains by two different registries, each emitting its own rooting block, so a fix
    // proven only through an endpoint leaves the handler path untested.
    opts.Discovery.DisableConventionalDiscovery()
        .IncludeType(typeof(AotMartenHandler));

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

    // GH-4843. The clustered half of this lane. A Balanced node that boots is not evidence that the
    // leader's assignment pass works in a native image; one that has assumed leadership AND been assigned
    // the durability agent has run NodeAgentController's election, EvaluateAssignments and the AssignAgent
    // round trip through the message router. Both are polled, because both happen after StartAsync
    // returns.
    if (!await app.WaitUntilAssumesLeadershipAsync(TimeSpan.FromSeconds(30)))
    {
        await Console.Error.WriteLineAsync("FAIL: the Balanced node never assumed leadership.");
        return 1;
    }

    if (!await waitForAgentAsync(app, PersistenceConstants.AgentScheme, TimeSpan.FromSeconds(30)))
    {
        await Console.Error.WriteLineAsync(
            $"FAIL: the leader never assigned itself a '{PersistenceConstants.AgentScheme}' agent. Running agents: {string.Join(", ", app.RunningAgents())}");
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

    // GH-4825. The message-handler side effect, through the HANDLER registry's rooting block rather than
    // the HTTP one. IMartenOp declares Execute itself, where IStartStream inherits it from IMartenOp, so
    // between them the two halves cover both arms of SideEffectPolicy.findMethod's lookup.
    var recorded = Guid.NewGuid();
    await app.Services.GetRequiredService<IMessageBus>().InvokeAsync(new RecordAotPing(recorded));

    // Vacuity guard: a 201 only proves the endpoint ran. The side effects must actually have run.
    await using (var session = app.Services.GetRequiredService<IDocumentStore>().LightweightSession())
    {
        var events = await session.Events.FetchStreamAsync(id);
        if (events.Count != 1)
        {
            await Console.Error.WriteLineAsync($"FAIL: expected the endpoint's IStartStream to append 1 event, found {events.Count}.");
            return 1;
        }

        if (await session.LoadAsync<AotPing>(recorded) == null)
        {
            await Console.Error.WriteLineAsync("FAIL: the handler returned an IMartenOp but no AotPing document was stored.");
            return 1;
        }
    }

    // Also part of the clustered path: a Balanced node stops its agents and releases leadership on the way
    // out, which is StopAgents and its siblings through the same routers.
    await app.StopAsync();

    Console.WriteLine("OK: Native AOT Marten HTTP boot (Balanced, leader, durability agent assigned) + IStartStream + Results<Ok<T>, ProblemHttpResult> + handler IMartenOp smoke passed.");
    return 0;
}
catch (Exception e)
{
    await Console.Error.WriteLineAsync("FAIL: Native AOT Marten HTTP smoke crashed:");
    await Console.Error.WriteLineAsync(e.ToString());
    return 1;
}

static async Task<bool> waitForAgentAsync(IHost host, string scheme, TimeSpan timeout)
{
    var deadline = DateTimeOffset.UtcNow + timeout;
    while (DateTimeOffset.UtcNow < deadline)
    {
        if (host.RunningAgents().Any(x => x.Scheme == scheme)) return true;
        await Task.Delay(100);
    }

    return host.RunningAgents().Any(x => x.Scheme == scheme);
}

public record AotOrderPlaced(Guid Id);

public class AotOrder
{
    public Guid Id { get; set; }
}

public record AotPing(Guid Id);

public record RecordAotPing(Guid Id);

public static class AotMartenHandler
{
    // Returns the IMartenOp INTERFACE, not the concrete op: the variable type is what
    // SideEffectPolicy.findMethod reflects over, and an interface is the shape whose Execute metadata the
    // trimmer drops.
    public static IMartenOp Handle(RecordAotPing command)
    {
        return MartenOps.Store(new AotPing(command.Id));
    }
}

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
