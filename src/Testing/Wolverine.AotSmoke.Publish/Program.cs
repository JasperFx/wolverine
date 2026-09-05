// AOT smoke test #3 (GH-4287) — see the csproj header for the full story. Boots a Wolverine host
// inside a REAL Native AOT binary through the ordinary public UseWolverine path, dispatches one
// message, and asserts the handler fired. Exit 0 only on the full boot + dispatch.
//
// `codegen write` (or any JasperFx CLI verb) refreshes the committed pre-gen under
// Internal/Generated/ — run it under plain `dotnet run`, never from the native binary.
using System.Diagnostics.CodeAnalysis;
using JasperFx;
using JasperFx.CodeGeneration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Wolverine;

var isCli = args.Length > 0 && args[0] is "codegen" or "describe" or "help" or "?";

var builder = Host.CreateDefaultBuilder(args)
    .UseWolverine(opts =>
    {
        opts.ServiceName = "aot-publish-smoke";
        opts.ApplicationAssembly = typeof(AotPublishPingHandler).Assembly;
        opts.Durability.Mode = DurabilityMode.Solo;
        opts.Discovery.DisableConventionalDiscovery().IncludeType(typeof(AotPublishPingHandler));

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

if (isCli)
{
    return await builder.RunJasperFxCommands(args);
}

AotRoots.Pin();

try
{
    using var host = builder.Build();
    await host.StartAsync();

    var bus = host.Services.GetRequiredService<IMessageBus>();
    await bus.InvokeAsync(new AotPublishPing(42));

    await host.StopAsync();

    if (AotPublishPingHandler.LastValue != 42)
    {
        await Console.Error.WriteLineAsync(
            $"FAIL: the host booted but the handler saw {AotPublishPingHandler.LastValue} instead of 42.");
        return 1;
    }

    Console.WriteLine("OK: Native AOT boot + dispatch smoke passed.");
    return 0;
}
catch (Exception e)
{
    await Console.Error.WriteLineAsync("FAIL: Native AOT boot smoke crashed:");
    await Console.Error.WriteLineAsync(e.ToString());
    return 1;
}

public record AotPublishPing(int Value);

public static class AotPublishPingHandler
{
    public static int LastValue;

    public static void Handle(AotPublishPing message) => LastValue = message.Value;
}

/// <summary>
/// The hand-written Native AOT roots that a TypeLoadMode.Static application needs TODAY
/// (GH-4287): the pre-generated registry and handler types are only ever located via
/// reflection, so without these ILC trims them and Static mode silently falls back to an
/// assembly scan that finds nothing. Direct construction is NOT enough — Activator and
/// GetMethods need reflection METADATA, which only [DynamicDependency] preserves.
/// jasperfx#743 tracks emitting this block from `codegen write` itself.
/// </summary>
internal static class AotRoots
{
    [DynamicDependency(DynamicallyAccessedMemberTypes.All,
        typeof(Internal.Generated.WolverineHandlers.GeneratedHandlerRegistry))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.All,
        typeof(Internal.Generated.WolverineHandlers.AotPublishPingHandler1993257527))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.All, typeof(AotPublishPingHandler))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.All, typeof(AotPublishPing))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.All,
        typeof(Wolverine.Runtime.Routing.MessageRouter<AotPublishPing>))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.All,
        typeof(Wolverine.Runtime.Routing.EmptyMessageRouter<AotPublishPing>))]
    internal static void Pin()
    {
    }
}
