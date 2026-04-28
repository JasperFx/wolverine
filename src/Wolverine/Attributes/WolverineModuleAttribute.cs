using System.Diagnostics.CodeAnalysis;
using JasperFx.Core.Reflection;
using JasperFx;

namespace Wolverine.Attributes;

/// <summary>
///     Marks the assembly as an automatically loaded Wolverine extension
///     module
/// </summary>
[AttributeUsage(AttributeTargets.Assembly)]
public class WolverineModuleAttribute : JasperFxAssemblyAttribute
{
    /// <summary>
    ///     Specify the IWolverineExtension type that should be automatically loaded
    ///     and applied when this assembly is present
    /// </summary>
    /// <param name="wolverineExtensionType"></param>
    /// <exception cref="ArgumentOutOfRangeException"></exception>
    public WolverineModuleAttribute(
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] Type wolverineExtensionType)
    {
        WolverineExtensionType = wolverineExtensionType;
        if (!wolverineExtensionType.CanBeCastTo<IWolverineExtension>())
        {
            throw new ArgumentOutOfRangeException(nameof(wolverineExtensionType),
                $"Has to be of type {nameof(IWolverineExtension)}");
        }
    }

    public WolverineModuleAttribute()
    {
    }

    [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)]
    public Type? WolverineExtensionType { get; }
}