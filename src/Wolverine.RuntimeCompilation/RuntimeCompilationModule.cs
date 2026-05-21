using Wolverine;
using Wolverine.Attributes;
using Wolverine.RuntimeCompilation;

[assembly: WolverineModule<RuntimeCompilationModule>]

namespace Wolverine.RuntimeCompilation;

/// <summary>
/// Auto-loaded Wolverine extension that activates whenever the
/// <c>WolverineFx.RuntimeCompilation</c> package is referenced by the
/// application. It registers the Roslyn-backed <c>IAssemblyGenerator</c> (via
/// <see cref="RuntimeCompilationExtensions.UseRuntimeCompilation"/>) so that
/// <c>TypeLoadMode.Dynamic</c>/<c>Auto</c> applications "just work" without an
/// explicit <c>opts.UseRuntimeCompilation()</c> call.
/// <para>
/// Core <c>WolverineFx</c> no longer ships the Roslyn runtime compiler; apps
/// that pre-generate everything with <c>TypeLoadMode.Static</c> simply don't
/// reference this package and therefore ship without Roslyn (smaller binaries,
/// faster cold start, AOT-friendliness). Calling <c>UseRuntimeCompilation()</c>
/// explicitly remains supported and is idempotent with this auto-registration.
/// </para>
/// </summary>
internal sealed class RuntimeCompilationModule : IWolverineExtension
{
    public void Configure(WolverineOptions options)
    {
        options.UseRuntimeCompilation();
    }
}
