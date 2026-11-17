using System.Reflection;
using System.Runtime.CompilerServices;
using Wolverine;
using Xunit;

namespace CoreTests.Compilation;

/// <summary>
///     GH-5150, plan decision D4: runtime-async is on by default for the net11.0 build, both for Wolverine's own
///     assemblies (WolverineRuntimeAsync in Directory.Build.props) and for runtime-compiled handler code
///     (CodeGeneration.UseRuntimeAsync). net10.0 cannot load runtime-async code, so it stays off there.
/// </summary>
public class runtime_async_defaults_5150
{
    // MethodImplAttributes.Async, the flag the compiler sets on a runtime-async method
    private const MethodImplAttributes AsyncFlag = (MethodImplAttributes)0x2000;

    [Fact]
    public void generated_handler_code_uses_runtime_async_by_default_only_on_net11()
    {
        var options = new WolverineOptions();

#if NET11_0_OR_GREATER
        options.CodeGeneration.UseRuntimeAsync.ShouldBeTrue();
#else
        options.CodeGeneration.UseRuntimeAsync.ShouldBeFalse();
#endif
    }

    [Fact]
    public void the_wolverine_assembly_is_compiled_with_runtime_async_only_on_net11()
    {
        var types = typeof(WolverineOptions).Assembly.GetTypes();

        var runtimeAsyncMethods = types
            .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance |
                                          BindingFlags.Static | BindingFlags.DeclaredOnly))
            .Count(m => (m.MethodImplementationFlags & AsyncFlag) != 0);

        var stateMachines = types.Count(t =>
            typeof(IAsyncStateMachine).IsAssignableFrom(t) && t.IsDefined(typeof(CompilerGeneratedAttribute)));

#if NET11_0_OR_GREATER
        runtimeAsyncMethods.ShouldBeGreaterThan(0);

        // Async iterators are not converted by runtime-async, so a handful of state machines remain
        stateMachines.ShouldBeLessThan(10);
#else
        runtimeAsyncMethods.ShouldBe(0);
        stateMachines.ShouldBeGreaterThan(100);
#endif
    }
}
