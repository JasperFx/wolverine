using Bobcat.Runtime;
using JasperFx.Environment;

namespace Bobcat;

/// <summary>
/// The container half of a <see cref="Preflight"/>: every check JasperFx knows how to collect from a host's
/// services. It lived on <see cref="Preflight"/> itself until Bobcat core stopped referencing JasperFx
/// (JasperFx/bobcat#744, GH-5082), so it is an extension method now, and a call site reads exactly as it did.
/// </summary>
public static class PreflightContainerChecks
{
    /// <summary>
    /// Adds every check the container knows about — <c>ISystemPart</c>, <c>IStatefulResource</c>
    /// and <c>IHealthCheck</c> — by delegating to JasperFx's own executor. The whole set is one
    /// preflight check: it fails with JasperFx's <see cref="EnvironmentCheckException"/>, whose message
    /// lists every failure, and that message is what <see cref="Preflight.Describe"/> reports.
    /// </summary>
    public static Preflight AddContainerChecks(this Preflight preflight, string description, IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(preflight);
        ArgumentNullException.ThrowIfNull(services);

        return preflight.Add(description, async token =>
        {
            var results = await EnvironmentChecker.ExecuteAllEnvironmentChecks(services, token);
            results.Assert(); // throws EnvironmentCheckException listing every failure
        });
    }
}
