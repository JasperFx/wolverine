namespace Wolverine.Attributes;

/// <summary>
///     Marks the assembly as an automatically loaded Wolverine extension
///     module
/// </summary>
[AttributeUsage(AttributeTargets.Assembly)]
public class WolverineModuleAttribute<T> : WolverineModuleAttribute
    where T : IWolverineExtension
{
    /// <summary>
    ///     Specify the IWolverineExtension type that should be automatically loaded
    ///     and applied when this assembly is present
    /// </summary>
    /// <param name="wolverineExtensionType"></param>
    /// <exception cref="ArgumentOutOfRangeException"></exception>
    public WolverineModuleAttribute() :
        base(typeof(T))
    {
    }
}