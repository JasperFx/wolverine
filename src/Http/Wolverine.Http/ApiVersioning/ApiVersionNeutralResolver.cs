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
    /// Returns true when the handler method (or, when absent, its declaring class) is decorated
    /// with <see cref="ApiVersionNeutralAttribute"/>. A method-level attribute overrides the class.
    /// </summary>
    public static bool IsNeutral(MethodInfo method)
    {
        if (method.GetCustomAttribute<ApiVersionNeutralAttribute>(inherit: false) is not null)
        {
            return true;
        }

        return method.DeclaringType?.GetCustomAttribute<ApiVersionNeutralAttribute>(inherit: false) is not null;
    }

    /// <summary>
    /// Validates that <c>[ApiVersion]</c> and <c>[ApiVersionNeutral]</c> are not both present on
    /// the same target (method or class). Mixing the two on a single target is contradictory and
    /// fails fast at startup. A method-level <c>[ApiVersionNeutral]</c> on a method inside a class
    /// that carries <c>[ApiVersion]</c> is permitted — the method opts out, siblings stay versioned.
    /// </summary>
    public static void ValidateNoConflict(MethodInfo method)
    {
        var methodHasApiVersion = method.GetCustomAttributes<ApiVersionAttribute>(inherit: false).Any();
        var methodHasNeutral = method.GetCustomAttribute<ApiVersionNeutralAttribute>(inherit: false) is not null;
        if (methodHasApiVersion && methodHasNeutral)
        {
            throw BuildConflict(MethodIdentity(method));
        }

        var declaringType = method.DeclaringType;
        if (declaringType is null) return;

        var classHasApiVersion = declaringType.GetCustomAttributes<ApiVersionAttribute>(inherit: false).Any();
        var classHasNeutral = declaringType.GetCustomAttribute<ApiVersionNeutralAttribute>(inherit: false) is not null;
        if (classHasApiVersion && classHasNeutral)
        {
            throw BuildConflict(declaringType.FullName ?? declaringType.Name);
        }
    }

    private static InvalidOperationException BuildConflict(string identity) =>
        new($"'{identity}' declares both [ApiVersion] and [ApiVersionNeutral]. " +
            "These attributes are mutually exclusive on the same target — pick one.");

    private static string MethodIdentity(MethodInfo method) =>
        (method.DeclaringType?.FullName ?? method.DeclaringType?.Name ?? "?") + "." + method.Name;
}
