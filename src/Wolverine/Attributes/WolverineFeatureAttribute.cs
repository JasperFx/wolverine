using System;
using Lamar;

namespace Wolverine.Attributes;

/// <summary>
///     Tells Wolverine to ignore this assembly in its determination of the application assembly
/// </summary>
[AttributeUsage(AttributeTargets.Assembly)]
public class WolverineFeatureAttribute : IgnoreAssemblyAttribute
{
}
