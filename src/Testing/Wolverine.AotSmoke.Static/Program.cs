// AOT smoke test #2 (wolverine#2746 sub-PR I).
//
// Companion to Wolverine.AotSmoke. That project covers the value-shape
// surface that is honestly AOT-clean today (Envelope, WolverineOptions
// builder, scheduling helpers — no codegen anywhere on the call paths it
// exercises). This project covers the *runtime* AOT story: developer
// pre-generates handler code via the JasperFx codegen-write CLI, commits
// the output, and ships a binary that loads under TypeLoadMode.Static
// without ever invoking the Roslyn pipeline.
//
// Boot recipe:
//   1. Configure a small handler set (one handler, one message).
//   2. Set GeneratedCodeMode = TypeLoadMode.Static — the StaticTypeLoader
//      reads pre-built MessageHandler types out of this assembly and
//      throws ExpectedTypeMissingException on miss.
//   3. Set AssertAllPreGeneratedTypesExist = true (Production + Development
//      so the smoke is environment-agnostic) — host throws at Compile
//      time if any chain's pre-built type can't be located.
//   4. Replace the default IAssemblyGenerator with a throwing sentinel.
//      Any silent fallback to Roslyn under Static mode triggers it.
//   5. Boot the host. Invoke a message. Wait for the handler to fire.
//      Exit 0 on success, non-zero with diagnostics on failure.
//
// Two distinct run-modes:
//
//   - "codegen write" (and other JasperFx CLI verbs)
//       Routed through RunJasperFxCommands so the codegen CLI can use
//       the same handler graph configuration to emit the pre-gen files
//       into Internal/Generated/. This is what's invoked when someone
//       runs the refresh procedure documented in the csproj.
//
//   - Default (no args / `dotnet run`)
//       The smoke verification path described in the boot recipe above.
//       CI runs this via build target CIAotSmoke.
//
// Refresh procedure when the codegen output format changes (the
// AotSmokeMessageHandler<hash>.cs file under Internal/Generated drifts):
//
//   dotnet run --project src/Testing/Wolverine.AotSmoke.Static codegen write
//
// then commit the regenerated files.

using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using JasperFx;
using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Model;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Wolverine;

// Sentinel IAssemblyGenerator. Replaces the default Roslyn-backed generator
// in DI; throws if the static-load path ever falls back to runtime
// compilation. Under TypeLoadMode.Static + AssertAllPreGeneratedTypesExist
// with the committed pre-gen present, this should never fire at runtime.
//
// Only swapped in for the runtime smoke path. The codegen-write path needs
// the real generator (codegen-write itself uses Roslyn to produce the source).
internal sealed class StaticModeViolatedException : Exception
{
    public StaticModeViolatedException()
        : base(
            "Wolverine.AotSmoke.Static booted under TypeLoadMode.Static but the runtime " +
            "still tried to invoke IAssemblyGenerator. The pre-generated handler under " +
            "Internal/Generated/WolverineHandlers/ is missing, stale, or the StaticTypeLoader " +
            "fell through to dynamic compilation. Re-run the codegen-write CLI against this " +
            "project and commit the regenerated Internal/Generated/ files.")
    {
    }
}

[UnconditionalSuppressMessage("Trimming", "IL2026",
    Justification = "Smoke sentinel. Throws unconditionally; never invokes the reflective generator surface it inherits.")]
[UnconditionalSuppressMessage("AOT", "IL3050",
    Justification = "Smoke sentinel. Throws unconditionally; never invokes the reflective generator surface it inherits.")]
internal sealed class ThrowingAssemblyGenerator : IAssemblyGenerator
{
    public string? AssemblyName
    {
        get => throw new StaticModeViolatedException();
        set => throw new StaticModeViolatedException();
    }

    public void ReferenceAssembly(Assembly assembly) =>
        throw new StaticModeViolatedException();

    public void ReferenceAssemblyContainingType<T>() =>
        throw new StaticModeViolatedException();

    public Assembly Generate(string code) =>
        throw new StaticModeViolatedException();

    public Assembly Generate(Action<ISourceWriter> source) =>
        throw new StaticModeViolatedException();

    public void Compile(GeneratedAssembly assembly, IServiceVariableSource? services) =>
        throw new StaticModeViolatedException();

    public void Compile(GeneratedAssembly assembly, IServiceVariableSource? services,
        out string code) =>
        throw new StaticModeViolatedException();
}

public static class Program
{
    private static readonly TaskCompletionSource<AotSmokeMessage> Received =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public static async Task<int> Main(string[] args)
    {
        // codegen-write (and any other JasperFx CLI verb) gets routed
        // through RunJasperFxCommands so the same handler graph
        // configuration that the smoke uses is what produces the pre-gen
        // files. Detect the dispatch by looking at args[0] — keeps the
        // detection trivial and matches the JasperFx CLI shape.
        var isCli = args.Length > 0 && IsKnownCliVerb(args[0]);

        try
        {
            var hostBuilder = Host.CreateDefaultBuilder(args)
                .UseWolverine(ConfigureWolverine);

            if (isCli)
            {
                // Real Roslyn generator stays registered — codegen-write
                // needs it to produce the source files.
                return await hostBuilder.RunJasperFxCommands(args);
            }

            // Runtime smoke path. Swap in the sentinel BEFORE Build()
            // so the static-load contract is enforced from the first
            // service resolution. Has to land here (not inside
            // ConfigureWolverine) because UseWolverine's
            // AddSingleton<IAssemblyGenerator, AssemblyGenerator>() runs
            // before the per-host opts lambda. See HostBuilderExtensions
            // .cs:124.
            hostBuilder.ConfigureServices(services =>
            {
                services.RemoveAll<IAssemblyGenerator>();
                services.AddSingleton<IAssemblyGenerator, ThrowingAssemblyGenerator>();
            });

            using var host = hostBuilder.Build();
            await host.StartAsync();

            var bus = host.Services.GetRequiredService<IMessageBus>();
            await bus.InvokeAsync(new AotSmokeMessage(42));

            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            timeout.Token.Register(() =>
                Received.TrySetException(new TimeoutException(
                    "AotSmokeMessage handler did not fire within 10s. Static-load may have silently failed.")));

            var got = await Received.Task;
            if (got.Value != 42)
            {
                await Console.Error.WriteLineAsync(
                    $"FAIL: handler fired but received unexpected payload {got.Value} (expected 42).");
                return 2;
            }

            Console.WriteLine("OK: TypeLoadMode.Static round-trip smoke passed.");
            await host.StopAsync();
            return 0;
        }
        catch (StaticModeViolatedException ex)
        {
            await Console.Error.WriteLineAsync($"FAIL: {ex.Message}");
            return 3;
        }
        catch (Exception ex)
        {
            await Console.Error.WriteLineAsync($"FAIL: unexpected exception during static-mode boot: {ex}");
            return 4;
        }
    }

    // JasperFx CLI verbs that Wolverine inherits via RunJasperFxCommands.
    // Conservative: only the verbs the smoke's refresh procedure exercises
    // (codegen + describe). If a future JasperFx CLI verb needs the
    // generator on, add it here.
    private static bool IsKnownCliVerb(string verb) =>
        verb == "codegen" || verb == "describe" || verb == "?" || verb == "help";

    internal static void ConfigureWolverine(WolverineOptions opts)
    {
        // The static-load contract under test. TypeLoadMode and
        // AssertAllPreGeneratedTypesExist live on different surfaces
        // (the former on Wolverine's CodeGeneration rules, the latter
        // on JasperFx's per-environment Profile). Set the assertion
        // for both Production and Development so the smoke is
        // environment-agnostic — same pre-gen contract enforced
        // regardless of the host's ASPNETCORE_ENVIRONMENT.
        opts.CodeGeneration.TypeLoadMode = TypeLoadMode.Static;
        opts.Services.CritterStackDefaults(cr =>
        {
            cr.Production.AssertAllPreGeneratedTypesExist = true;
            cr.Development.AssertAllPreGeneratedTypesExist = true;
        });

        // Constrain handler discovery to exactly the smoke's handler so
        // the committed pre-gen has a stable, minimal shape — irrelevant
        // assemblies / built-in handlers don't get drawn in.
        opts.Discovery.DisableConventionalDiscovery()
            .IncludeType<AotSmokeHandler>();
    }

    internal static void Notify(AotSmokeMessage message)
    {
        Received.TrySetResult(message);
    }
}

public sealed record AotSmokeMessage(int Value);

public sealed class AotSmokeHandler
{
    public void Handle(AotSmokeMessage message)
    {
        Program.Notify(message);
    }
}
