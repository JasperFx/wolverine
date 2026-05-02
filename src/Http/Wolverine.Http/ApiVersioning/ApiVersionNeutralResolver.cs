using System.Reflection;
using Asp.Versioning;

namespace Wolverine.Http.ApiVersioning;

/// <summary>
/// Companion to <see cref="ApiVersionResolver"/> that detects <see cref="ApiVersionNeutralAttribute"/>
/// on a handler method (or its declaring class) and validates it is not combined with
/// <see cref="ApiVersionAttribute"/> on the same target.
/// </summary>
internal static class ApiVersionNeutralResolver
{
    /// <summary>
    /// Reads the <c>[ApiVersion]</c> and <c>[ApiVersionNeutral]</c> attribute presence from the
    /// method and its declaring class in a single reflection pass. Throws if the same target
    /// (method or class) declares both. Method-level attributes win over class-level attributes
    /// per the documented rule.
    /// </summary>
    /// <returns><c>true</c> when the chain is version-neutral, otherwise <c>false</c>.</returns>
    public static bool Resolve(MethodInfo method)
    {
        var methodHasApiVersion = method.GetCustomAttributes<ApiVersionAttribute>(inherit: false).Any();
        var methodHasNeutral = method.GetCustomAttribute<ApiVersionNeutralAttribute>(inherit: false) is not null;
        if (methodHasApiVersion && methodHasNeutral)
        {
            throw BuildConflict(MethodIdentity(method));
        }

        var declaringType = method.DeclaringType;
        var classHasApiVersion = false;
        var classHasNeutral = false;
        if (declaringType is not null)
        {
            classHasApiVersion = declaringType.GetCustomAttributes<ApiVersionAttribute>(inherit: false).Any();
            classHasNeutral = declaringType.GetCustomAttribute<ApiVersionNeutralAttribute>(inherit: false) is not null;
            if (classHasApiVersion && classHasNeutral)
            {
                throw BuildConflict(declaringType.FullName ?? declaringType.Name);
            }
        }

        // Method-level wins. A method-level [ApiVersion] takes the chain out of class-level
        // neutrality, just as a method-level [ApiVersionNeutral] takes the chain out of
        // class-level versioning.
        if (methodHasApiVersion)
        {
            return false;
        }

        if (methodHasNeutral)
        {
            return true;
        }

        return classHasNeutral;
    }

    /// <summary>
    /// Returns true when the handler method (or, when absent, its declaring class) is decorated
    /// with <see cref="ApiVersionNeutralAttribute"/>. A method-level <c>[ApiVersion]</c> overrides
    /// class-level <c>[ApiVersionNeutral]</c> (and vice versa). Throws on same-target conflict.
    /// </summary>
    public static bool IsNeutral(MethodInfo method) => Resolve(method);

    /// <summary>
    /// Validates that <c>[ApiVersion]</c> and <c>[ApiVersionNeutral]</c> are not both present on
    /// the same target (method or class). Mixing the two on a single target is contradictory and
    /// fails fast at startup. A method-level <c>[ApiVersionNeutral]</c> on a method inside a class
    /// that carries <c>[ApiVersion]</c> is permitted — the method opts out, siblings stay versioned.
    /// </summary>
    public static void ValidateNoConflict(MethodInfo method) => Resolve(method);

    private static InvalidOperationException BuildConflict(string identity) =>
        new($"'{identity}' declares both [ApiVersion] and [ApiVersionNeutral]. " +
            "These attributes are mutually exclusive on the same target — pick one.");

    private static string MethodIdentity(MethodInfo method) =>
        (method.DeclaringType?.FullName ?? method.DeclaringType?.Name ?? "?") + "." + method.Name;
}
