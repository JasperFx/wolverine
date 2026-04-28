using JasperFx.CodeGeneration;
using JasperFx.RuntimeCompiler;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Wolverine;

/// <summary>
/// Opt-in extension methods that enable runtime Roslyn compilation of Wolverine
/// generated code. Add the <c>WolverineFx.RuntimeCompilation</c> package and call
/// <see cref="UseRuntimeCompilation"/> from <c>UseWolverine(...)</c> if your
/// application uses <see cref="JasperFx.CodeGeneration.TypeLoadMode.Dynamic"/>
/// or <see cref="JasperFx.CodeGeneration.TypeLoadMode.Auto"/> mode (the
/// development defaults), OR if you pre-generate code with
/// <see cref="JasperFx.CodeGeneration.TypeLoadMode.Static"/> but want a fallback
/// to runtime compilation for missing files.
/// <para>
/// Production deployments that pre-generate ALL code with
/// <see cref="JasperFx.CodeGeneration.TypeLoadMode.Static"/> do not need this
/// package and can ship without Roslyn — smaller binaries, faster cold start,
/// and AOT compatibility.
/// </para>
/// </summary>
public static class RuntimeCompilationExtensions
{
    /// <summary>
    /// Register the Roslyn-backed <see cref="IAssemblyGenerator"/>
    /// (<see cref="AssemblyGenerator"/>) so Wolverine can compile generated
    /// handler/middleware code at runtime. Required when running with
    /// <see cref="JasperFx.CodeGeneration.TypeLoadMode.Dynamic"/> mode or when
    /// <see cref="JasperFx.CodeGeneration.TypeLoadMode.Auto"/>/<see cref="JasperFx.CodeGeneration.TypeLoadMode.Static"/>
    /// fall back to compilation because pre-generated code is missing.
    /// <para>
    /// Idempotent — safe to call multiple times. If <see cref="IAssemblyGenerator"/>
    /// is already registered, this is a no-op.
    /// </para>
    /// </summary>
    /// <param name="options">The Wolverine options being configured.</param>
    /// <returns>The same <paramref name="options"/> for chaining.</returns>
    public static WolverineOptions UseRuntimeCompilation(this WolverineOptions options)
    {
        if (options is null) throw new ArgumentNullException(nameof(options));

        options.Services.TryAddSingleton<IAssemblyGenerator, AssemblyGenerator>();

        return options;
    }

    /// <summary>
    /// Convenience overload that registers <see cref="IAssemblyGenerator"/>
    /// directly on an <see cref="IServiceCollection"/> for advanced scenarios
    /// where the registration must happen outside of <c>UseWolverine(...)</c>.
    /// </summary>
    public static IServiceCollection AddWolverineRuntimeCompilation(this IServiceCollection services)
    {
        if (services is null) throw new ArgumentNullException(nameof(services));

        services.TryAddSingleton<IAssemblyGenerator, AssemblyGenerator>();

        return services;
    }
}
