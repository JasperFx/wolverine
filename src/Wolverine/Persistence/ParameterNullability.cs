using System.Reflection;
using JasperFx.Core.Reflection;

namespace Wolverine.Persistence;

/// <summary>
///     Reads a parameter's nullable annotation, which several persistence attributes use as the default
///     for <see cref="IDataRequirement.Required" />.
/// </summary>
/// <remarks>
///     GH-3916 introduced this on <c>WriteModelAttribute</c>; GH-3929 gave <c>ReadModelAttribute</c> the
///     same behaviour, so it lives here rather than being copied a second time.
/// </remarks>
internal static class ParameterNullability
{
    /// <summary>
    ///     A parameter is nullable when it is a <c>Nullable&lt;T&gt;</c> value type, or a reference type
    ///     whose nullable annotation context marks it nullable.
    /// </summary>
    /// <remarks>
    ///     In an assembly compiled with <c>&lt;Nullable&gt;disable&lt;/Nullable&gt;</c> a reference type
    ///     parameter reads as <see cref="NullabilityState.Unknown" /> rather than
    ///     <see cref="NullabilityState.Nullable" />, so this returns <c>false</c> and callers keep their
    ///     historical "required" default. Nullability inference is therefore a no-op for those codebases
    ///     rather than a silent behaviour change.
    ///     <para>
    ///     A fresh <see cref="NullabilityInfoContext" /> per call keeps this thread-safe across concurrent
    ///     chain compilation.
    ///     </para>
    /// </remarks>
    internal static bool IsNullableAnnotated(ParameterInfo parameter)
    {
        if (parameter.ParameterType.IsValueType)
        {
            return parameter.ParameterType.IsNullable();
        }

        return new NullabilityInfoContext().Create(parameter).WriteState == NullabilityState.Nullable;
    }
}
