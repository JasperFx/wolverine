using System.Diagnostics.CodeAnalysis;

namespace Wolverine.Attributes;

/// <summary>
///     Marks the assembly as an automatically loaded Wolverine extension module
/// </summary>
[AttributeUsage(AttributeTargets.Assembly)]
public class WolverineModuleAttribute<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] T> : WolverineModuleAttribute
    where T : IWolverineExtension
{
    /// <summary>
    ///     Specify the IWolverineExtension type that should be automatically loaded
    ///     and applied when this assembly is present
    /// </summary>
    public WolverineModuleAttribute() :
        base(typeof(T))
    {
    }
}