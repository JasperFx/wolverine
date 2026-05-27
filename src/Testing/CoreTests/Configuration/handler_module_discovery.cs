using Module2;
using Shouldly;
using Wolverine.Configuration;
using Xunit;

namespace CoreTests.Configuration;

// GH-2905: [WolverineHandlerModule] assemblies are discovered via the shared deployment-list module
// loader (ModuleAssemblyLoader) + an enumeration of loaded assemblies, with no AssemblyFinder
// filesystem probe. Module2 is marked [assembly: WolverineHandlerModule].
public class handler_module_discovery
{
    [Fact]
    public void discovers_handler_module_assemblies_without_a_filesystem_probe()
    {
        var discovery = new HandlerDiscovery();

        discovery.DiscoverHandlerModules(typeof(handler_module_discovery).Assembly);

        discovery.Assemblies.ShouldContain(typeof(Module2Message1).Assembly);
    }
}
