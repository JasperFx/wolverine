using JasperFx.CodeGeneration.Model;

namespace Wolverine;

public sealed partial class WolverineOptions
{
    /// <summary>
    /// Restore every runtime default that changed between Wolverine 5.x
    /// and 6.x back to its Wolverine 5.x value. Use this on upgrade when
    /// you want to adopt Wolverine 6's API surface, NuGet line, target
    /// frameworks and bug fixes without simultaneously adopting its new
    /// runtime defaults — typically a temporary step in a large
    /// application's migration.
    ///
    /// <para><b>Defaults restored as of 6.0:</b></para>
    /// <list type="bullet">
    ///   <item>
    ///     <description>
    ///       <see cref="WolverineOptions.ServiceLocationPolicy"/> set to
    ///       <see cref="ServiceLocationPolicy.AllowedButWarn"/> (6.x default:
    ///       <see cref="ServiceLocationPolicy.NotAllowed"/>). See wolverine#2584.
    ///     </description>
    ///   </item>
    /// </list>
    ///
    /// <para><b>Not handled by this method</b> (because they aren't runtime defaults):</para>
    /// <list type="bullet">
    ///   <item>
    ///     <description>
    ///       Target framework (net8.0 dropped in 6.0; can't be flipped at runtime).
    ///     </description>
    ///   </item>
    ///   <item>
    ///     <description>
    ///       Default JSON serializer. System.Text.Json has been the default since
    ///       <b>Wolverine 5.0</b> — there is no 5.x Newtonsoft default to restore.
    ///       If you want Newtonsoft as the default (the 4.x-and-earlier behavior),
    ///       install <c>WolverineFx.Newtonsoft</c>, add <c>using Wolverine.Newtonsoft;</c>
    ///       and call <c>opts.UseNewtonsoftForSerialization()</c> explicitly.
    ///     </description>
    ///   </item>
    ///   <item>
    ///     <description>
    ///       Package layout changes (e.g. Newtonsoft moved to
    ///       <c>WolverineFx.Newtonsoft</c>). Resolved by installing the relevant
    ///       NuGet package, not by a runtime flag.
    ///     </description>
    ///   </item>
    /// </list>
    ///
    /// <para><b>Where to call this:</b> inside your <c>UseWolverine</c> lambda,
    /// once, near the top — before any code that reads a default whose 5.x
    /// value the rest of your configuration depends on.</para>
    ///
    /// <code>
    ///   builder.Host.UseWolverine(opts =>
    ///   {
    ///       opts.RestoreV5Defaults();
    ///       // … the rest of your configuration
    ///   });
    /// </code>
    ///
    /// See the <see href="https://wolverinefx.net/guide/migration.html">migration
    /// guide</see> for the complete list of 6.0 changes (including build-time
    /// and package-layout changes that this method doesn't address).
    /// </summary>
    public void RestoreV5Defaults()
    {
        // ServiceLocationPolicy default flipped in 6.0 (wolverine#2584).
        // Restoring the 5.x value gets the old behavior: codegen falls back
        // to service-location at runtime for opaque registrations rather
        // than throwing InvalidServiceLocationException at host build.
        // The property lives directly on WolverineOptions (not on the
        // CodeGeneration GenerationRules), so we set it via `this`.
        ServiceLocationPolicy = ServiceLocationPolicy.AllowedButWarn;

        // Add new lines here as additional defaults flip between 6.0 and
        // 6.x. Keep the RestoreV5Defaults_resets_every_flipped_default test
        // in lockstep — it asserts every default this method touches still
        // differs from a fresh-WolverineOptions value.
    }
}
