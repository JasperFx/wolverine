using Microsoft.Extensions.Hosting;
using Wolverine;

namespace Module1;

/// <summary>
/// GH-4156. Reconstructs the layout reported against `AssertPreBuiltTypesExist`: a class library holds both
/// the handlers AND the composition root, so it is the assembly that calls <c>UseWolverine</c> and therefore
/// the one Wolverine infers as <see cref="WolverineOptions.ApplicationAssembly" />. The entry project is a
/// separate assembly, and `codegen write` emits its source there -- so with the default conventions the two
/// are always different assemblies and <c>TypeLoadMode.Static</c> cannot resolve a single pre-built type.
///
/// <para>Deliberately in this library rather than in the test assembly: calling <c>UseWolverine</c> from
/// CoreTests would infer CoreTests, which is the configuration that WORKS and proves nothing.</para>
/// </summary>
public static class CompositionRootInAClassLibrary
{
    /// <summary>
    /// Registers AND builds, both inside this library. Both halves matter: UseWolverine on IHostBuilder
    /// defers its real work into a ConfigureServices callback that only runs during Build(), so the frame
    /// the application-assembly inference actually sees is the one that called Build(). Splitting them --
    /// registering here and building in the test assembly -- infers the TEST assembly and reproduces
    /// nothing.
    /// </summary>
    public static IHost BuildHost(Action<WolverineOptions>? configure = null)
    {
        return Host.CreateDefaultBuilder().UseWolverine(opts =>
        {
            opts.Discovery.DisableConventionalDiscovery().IncludeType(typeof(Bug4156PingHandler));
            configure?.Invoke(opts);
        }).Build();
    }
}

public record Bug4156Ping;

public static class Bug4156PingHandler
{
    public static void Handle(Bug4156Ping ping)
    {
    }
}
