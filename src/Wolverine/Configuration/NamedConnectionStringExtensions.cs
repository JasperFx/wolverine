using JasperFx.Core;
using Microsoft.Extensions.Configuration;

namespace Wolverine.Configuration;

public static class NamedConnectionStringExtensions
{
    /// <summary>
    /// GH-4527. Declare that this application needs a connection string resolved by name from
    /// <c>IConfiguration</c>, so the runtime validates it in its aggregated startup check -- failing once
    /// naming <b>every</b> missing name, before the message store migrates and before any transport connects.
    ///
    /// <para>
    /// Any API that resolves a connection string by name should call this, so they all report identically
    /// and a developer sees every missing name on one run rather than one per deploy.
    /// </para>
    ///
    /// <para>
    /// The issue also asked for a JasperFx environment check per dependency, so <c>check-env</c> could gate a
    /// deploy. That is deliberately NOT registered here: <c>IServiceCollection.CheckEnvironment</c> carries
    /// <c>[RequiresUnreferencedCode]</c> ("Routes through AddJasperFx which scans assemblies for
    /// IJasperFxCommand types"), and both Wolverine core and Wolverine.RabbitMQ declare
    /// <c>IsAotCompatible=true</c> -- so calling it from either would forfeit that claim (IL2026). It needs a
    /// trim-safe registration path upstream in JasperFx first.
    /// </para>
    /// </summary>
    /// <param name="kind">What needs it, for the message -- e.g. "Rabbit MQ", "Kafka".</param>
    /// <param name="connectionStringName">The connection string key.</param>
    /// <param name="aspireResourceMethod">
    /// The Aspire AppHost builder method that registers this resource (e.g. <c>AddRabbitMQ</c>), named in the
    /// failure message because a missing Aspire reference is the dominant cause. Null when there is none
    /// worth naming.
    /// </param>
    public static WolverineOptions RequireNamedConnectionString(this WolverineOptions options, string kind,
        string connectionStringName, string? aspireResourceMethod = null)
    {
        var dependency = new NamedConfigurationDependency(kind, connectionStringName, aspireResourceMethod);

        if (!options.NamedConfigurationDependencies.Contains(dependency))
        {
            options.NamedConfigurationDependencies.Add(dependency);
        }

        return options;
    }

    /// <summary>
    /// The single-dependency failure, built the same way the aggregated startup check builds its multi-name
    /// one, so the environment check, the startup check and the DI factory backstop all read identically.
    /// </summary>
    internal static MissingNamedConnectionStringsException MissingException(this NamedConfigurationDependency dependency,
        IConfiguration configuration)
    {
        var configured = configuration.GetSection("ConnectionStrings").GetChildren()
            .Select(x => x.Key)
            .OrderBy(x => x)
            .ToArray();

        return new MissingNamedConnectionStringsException([dependency], configured);
    }
}
