using JasperFx;

namespace Wolverine.Attributes;

/// <summary>
/// Assembly-level marker attribute that designates an assembly 
/// as containing Wolverine message handlers for automatic discovery.
/// </summary>
[AttributeUsage(AttributeTargets.Assembly)]
public class WolverineHandlerModuleAttribute : JasperFxAssemblyAttribute;
