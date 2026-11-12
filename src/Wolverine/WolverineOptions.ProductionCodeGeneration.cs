using JasperFx;
using JasperFx.CodeGeneration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Wolverine;

public sealed partial class WolverineOptions
{
    // A value no application can mean. Written over Production.GeneratedCodeMode before any of the application's own
    // JasperFxOptions configuration runs, so that afterwards "still the sentinel" means "the application never set it",
    // including the case where it explicitly set Dynamic, the value JasperFx's Production profile already had.
    internal const TypeLoadMode UnsetProductionCodeMode = (TypeLoadMode)(-1);

    /// <summary>
    /// GH-5523 (7.0): a Wolverine application in the Production profile defaults to pre-generated code,
    /// <see cref="TypeLoadMode.Static"/> with <see cref="Profile.AssertAllPreGeneratedTypesExist"/> on. The shared
    /// JasperFx default (Dynamic, no assertion) stays as it is, so a Marten, Polecat or Fisher application without
    /// Wolverine is unchanged. Only the Production profile is touched; Development keeps runtime compilation.
    ///
    /// <para>Both hooks are inserted at the front of the service collection. The first runs before any of the
    /// application's <c>CritterStackDefaults()</c> / <c>Configure&lt;JasperFxOptions&gt;()</c> calls, wherever they
    /// were registered, and marks the code mode as unset; the second runs before any other post-configuration and
    /// applies the 7.0 values only where the application left them alone. An explicit setting always wins.</para>
    /// </summary>
    internal void ApplyProductionCodeGenerationDefault(IServiceCollection services)
    {
        bool assertionBefore = false;

        services.Insert(0, ServiceDescriptor.Singleton<IConfigureOptions<JasperFxOptions>>(
            new ConfigureOptions<JasperFxOptions>(jasperfx =>
            {
                assertionBefore = jasperfx.Production.AssertAllPreGeneratedTypesExist;
                MarkProductionCodeGenerationUnset(jasperfx);
            })));

        services.Insert(0, ServiceDescriptor.Singleton<IPostConfigureOptions<JasperFxOptions>>(
            new PostConfigureOptions<JasperFxOptions>(Options.DefaultName,
                jasperfx => ResolveProductionCodeGeneration(jasperfx, assertionBefore))));
    }

    internal static void MarkProductionCodeGenerationUnset(JasperFxOptions jasperfx)
    {
        jasperfx.Production.GeneratedCodeMode = UnsetProductionCodeMode;
        jasperfx.Production.AssertAllPreGeneratedTypesExist = true;
    }

    /// <summary>
    /// Resolve the Production profile's code generation after the application's own configuration has run.
    /// <paramref name="assertionBefore"/> is the value <see cref="Profile.AssertAllPreGeneratedTypesExist"/> had
    /// before <see cref="MarkProductionCodeGenerationUnset"/> ran, which is JasperFx's own default.
    /// </summary>
    internal void ResolveProductionCodeGeneration(JasperFxOptions jasperfx, bool assertionBefore)
    {
        var production = jasperfx.Production;
        var modeSetByApplication = production.GeneratedCodeMode != UnsetProductionCodeMode;

        if (modeSetByApplication)
        {
            // The application chose its own code generation mode. Wolverine's assertion default only makes sense
            // alongside Static, so it is withdrawn unless the application chose Static itself. An application that
            // sets a mode other than Static AND explicitly turns the assertion on is the one combination this cannot
            // tell apart from the default; it gets JasperFx's own value for the assertion.
            if (production.GeneratedCodeMode != TypeLoadMode.Static && production.AssertAllPreGeneratedTypesExist)
            {
                production.AssertAllPreGeneratedTypesExist = assertionBefore;
            }

            return;
        }

        if (!V6DefaultsRestored && !ApplyProductionCodeGenerationUnderTestRunner && isRunningUnderATestRunner())
        {
            // Decision on GH-5523: a test host built with Host.CreateDefaultBuilder() and no DOTNET_ENVIRONMENT is
            // in the Production environment, and has no pre-generated code. Applying the 7.0 default there would
            // fail every such test at startup, in this repository and in every application's own test suite, so a
            // process with a test framework loaded keeps JasperFx's Production profile. Logged at startup.
            ProductionCodeGenerationSkippedForTestRunner = true;
        }

        if (V6DefaultsRestored || ProductionCodeGenerationSkippedForTestRunner)
        {
            // Wolverine 6.x: JasperFx's own Production profile, runtime compilation. An application that turned the
            // assertion off keeps it off; one that turned it on without choosing a mode cannot be told apart from
            // the default and gets JasperFx's value (6.x apps that assert also set Static, and return above).
            production.GeneratedCodeMode = TypeLoadMode.Dynamic;
            if (production.AssertAllPreGeneratedTypesExist)
            {
                production.AssertAllPreGeneratedTypesExist = assertionBefore;
            }

            return;
        }

        production.GeneratedCodeMode = TypeLoadMode.Static;
        // AssertAllPreGeneratedTypesExist was set to true by MarkProductionCodeGenerationUnset. If the application
        // turned it off, that wins and is left alone.
    }

    /// <summary>
    /// True when the 7.0 Production code generation default (GH-5523) was not applied because this process is a
    /// test host. Surfaced in the startup log.
    /// </summary>
    internal bool ProductionCodeGenerationSkippedForTestRunner { get; private set; }

    /// <summary>
    /// Testing hook: apply the 7.0 Production code generation default even though a test framework is loaded.
    /// </summary>
    internal bool ApplyProductionCodeGenerationUnderTestRunner { get; set; }

    // Narrower than IsTestRunnerAssembly on purpose: a false match here silently turns the 7.0 default off in a real
    // production application, so nothing that ships at runtime (JetBrains.Annotations, say) may match.
    private static readonly string[] TestFrameworkAssemblyPrefixes =
    [
        "xunit", "nunit", "MSTest", "TUnit", "testhost", "ReSharperTestRunner", "Microsoft.Testing.Platform"
    ];

    private static bool isRunningUnderATestRunner()
    {
        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            var name = assembly.GetName().Name;
            if (name != null && TestFrameworkAssemblyPrefixes.Any(x => name.StartsWith(x, StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }
        }

        return false;
    }
}
