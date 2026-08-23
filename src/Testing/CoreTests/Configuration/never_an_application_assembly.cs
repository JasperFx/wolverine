using System.Reflection;
using System.Reflection.Emit;
using JasperFx;
using Shouldly;
using Wolverine;
using Xunit;

namespace CoreTests.Configuration;

/// <summary>
/// GH-3778. The stack walk that picks an application assembly — and feeds the divergence warning —
/// knew about System*/Microsoft*/test runners and nothing else, so it happily adopted assemblies that
/// cannot be an application. Measured on a fully green CoreTests run, 41 of 46 warnings named a
/// runtime-compiled assembly (Roslyn's random "ofqrxydn.tlz" names, i.e. Wolverine's own generated
/// code) and 5 named JasperFx. After this predicate: 46 warnings became 1, and that 1 is real.
/// </summary>
public class never_an_application_assembly
{
    [Fact]
    public void a_dynamic_assembly_is_never_the_application()
    {
        var dynamic = AssemblyBuilder.DefineDynamicAssembly(
            new AssemblyName("some_dynamic_assembly"), AssemblyBuilderAccess.Run);

        WolverineOptions.IsNeverAnApplicationAssembly(dynamic).ShouldBeTrue();
    }

    [Fact]
    public void jasperfx_itself_is_never_the_application()
    {
        // JasperFx carries no ignore marker of its own -- core Wolverine excludes itself with
        // [assembly: WolverineIgnore], JasperFx has nothing equivalent -- so it has to be named.
        WolverineOptions.IsNeverAnApplicationAssembly(typeof(JasperFxOptions).Assembly).ShouldBeTrue();
    }

    [Fact]
    public void a_normal_assembly_on_disk_still_qualifies()
    {
        // The guard rail: this must stay narrow, or the walk skips the real application and falls
        // back to the entry assembly.
        WolverineOptions.IsNeverAnApplicationAssembly(GetType().Assembly).ShouldBeFalse();
        WolverineOptions.IsNeverAnApplicationAssembly(typeof(Module1.IModuleService).Assembly).ShouldBeFalse();
    }
}
