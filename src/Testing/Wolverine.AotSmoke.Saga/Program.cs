// AOT smoke test #5 (GH-4765) — see the csproj header for the full story. Boots a SQLITE-BACKED
// Wolverine host inside a REAL Native AOT binary and drives a saga through it, which is the first time
// any CI lane has executed a persistence-composed chain in a native image.
//
// Exit 0 only when the saga's state actually changed. Every failure path says which stage broke, because
// the point of this lane is to tell "the saga frame was trimmed" apart from "nothing was discovered at
// all" apart from "the store did not persist".
//
// `codegen write` refreshes the committed pre-gen under Internal/Generated/ — run it under plain
// `dotnet run` from THIS directory, never from the native binary.
using System.Text.Json;
using System.Text.Json.Serialization;
using JasperFx;
using JasperFx.CodeGeneration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Wolverine;
using Wolverine.Persistence.Sagas;
using Wolverine.Sqlite;

var isCli = args.Length > 0 && args[0] is "codegen" or "describe" or "help" or "?";

// A fresh file per run. A database left behind by a previous run could let the assertions below pass on
// stale rows — the same class of measurement error the perf rig's per-run schema drop exists to avoid.
var databasePath = Path.Combine(Path.GetTempPath(), $"wolverine_aot_saga_{Guid.NewGuid():n}.db");

var builder = Host.CreateApplicationBuilder(args);

builder.UseWolverine(opts =>
{
    opts.ServiceName = "aot-saga-smoke";

    // Solo: one SQLite file takes one writer, and a native smoke has no business electing a leader.
    opts.Durability.Mode = DurabilityMode.Solo;

    opts.PersistMessagesWithSqlite($"Data Source={databasePath}")
        // GH-4805. The seam this lane exists to prove: without it, the first saga insert in a native
        // image throws "Reflection-based serialization has been disabled for this application" and an
        // application has no way to intervene -- the schema used JsonSerializerOptions.Default.
        .UseSagaSerializerOptions(new JsonSerializerOptions { TypeInfoResolver = AotSagaJsonContext.Default });

    opts.Discovery.DisableConventionalDiscovery()
        .IncludeType(typeof(AotSaga));

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

var host = builder.Build();

if (isCli)
{
    return await host.RunJasperFxCommands(args);
}

try
{
    await host.StartAsync();

    var bus = host.Services.GetRequiredService<IMessageBus>();
    var id = Guid.NewGuid();

    // Start the saga, then advance it. Two messages rather than one so the lane covers the LOAD path as
    // well as the start path: loading is where ISagaStorage<TId, TSaga> — the variable type that made
    // de-genericizing this frame impossible, and rooting it necessary — actually gets used.
    await bus.InvokeAsync(new StartAotSaga(id));
    await bus.InvokeAsync(new AdvanceAotSaga(id));

    // The vacuity guard, and the whole reason this lane is worth having. Wolverine.AotSmoke.Http booted
    // clean in a native image having discovered ZERO endpoints before its guard was added; the
    // store-backed equivalent is a host that starts, finds no saga chain, invokes nothing, exits 0 and
    // tells us the rooting works when it was never tested.
    if (!AotSaga.Advanced)
    {
        await Console.Error.WriteLineAsync(
            "FAIL: the host booted and both messages were invoked, but the saga never handled AdvanceAotSaga. Either the saga chain was not discovered, or the saga was not loaded back out of the store.");
        return 1;
    }

    await host.StopAsync();

    Console.WriteLine("OK: Native AOT Sqlite-backed saga smoke passed.");
    return 0;
}
catch (Exception e)
{
    await Console.Error.WriteLineAsync("FAIL: Native AOT saga smoke crashed:");
    await Console.Error.WriteLineAsync(e.ToString());
    return 1;
}
finally
{
    try
    {
        if (File.Exists(databasePath)) File.Delete(databasePath);
    }
    catch
    {
        // A leftover temp file is not worth failing the lane over.
    }
}

[JsonSerializable(typeof(AotSaga))]
internal partial class AotSagaJsonContext : JsonSerializerContext;

public record StartAotSaga(Guid Id);

public record AdvanceAotSaga(Guid Id);

/// <summary>
///     The reason this lane exists. <c>SagaPersistenceChainPolicy</c> closes
///     <c>EnrollAndFetchSagaStorageFrame&lt;Guid, AotSaga&gt;</c> over this type during POLICY
///     application, which runs at startup under <see cref="TypeLoadMode.Static" /> with the generated code
///     already compiled in. Nothing names that instantiation statically, so it survives only because the
///     frame contributes its own root through <c>IAotRootSource</c> (GH-4765 / #4801).
/// </summary>
public class AotSaga : Saga
{
    /// <summary>
    ///     Static, because the assertion has to survive the saga instance being persisted and reloaded —
    ///     and because what is being proven is that the handler RAN, not what the row contains.
    /// </summary>
    public static bool Advanced;

    public Guid Id { get; set; }

    public static AotSaga Start(StartAotSaga command)
    {
        return new AotSaga { Id = command.Id };
    }

    public void Handle(AdvanceAotSaga command)
    {
        Advanced = true;
    }
}
