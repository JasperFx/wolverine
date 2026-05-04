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
    /// Display name for <see cref="ApiVersionAttribute"/> as it appears in conflict messages and tests.
    /// Defined here as the single source of truth so the resolver template strings and the asserting
    /// tests cannot drift apart silently.
    /// </summary>
    internal const string ApiVersionAttributeName = "[ApiVersion]";

    /// <summary>
    /// Display name for <see cref="ApiVersionNeutralAttribute"/> as it appears in conflict messages and tests.
    /// Defined here as the single source of truth so the resolver template strings and the asserting
    /// tests cannot drift apart silently.
    /// </summary>
    internal const string ApiVersionNeutralAttributeName = "[ApiVersionNeutral]";

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

    private static InvalidOperationException BuildConflict(string identity) =>
        new($"'{identity}' declares both {ApiVersionAttributeName} and {ApiVersionNeutralAttributeName}. " +
            "These attributes are mutually exclusive on the same target — pick one.");

    private static string MethodIdentity(MethodInfo method) =>
        (method.DeclaringType?.FullName ?? method.DeclaringType?.Name ?? "?") + "." + method.Name;
}
