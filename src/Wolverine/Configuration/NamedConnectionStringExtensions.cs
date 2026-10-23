using JasperFx.Core;
using Microsoft.Extensions.Configuration;
using Wolverine.Runtime;

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
    /// GH-5126. Declare a connection string this application resolves by name from <c>IConfiguration</c>
    /// (exactly as <see cref="RequireNamedConnectionString"/> does, so it joins the aggregated startup check),
    /// <b>and</b> hand the resolved value to <paramref name="apply"/> once the container is built -- before the
    /// message store initializes, before any transport connects, and before the <c>resources</c> commands
    /// touch a broker.
    ///
    /// <para>
    /// This is the shared plumbing behind the <c>Use{Transport}UsingNamedConnection(name)</c> and
    /// <c>AddNamed{Transport}BrokerUsingNamedConnection(broker, name)</c> APIs. A transport's configuration is
    /// built before <c>IConfiguration</c> can be read, so the callback is how the value reaches the transport
    /// -- typically by setting the same property the transport's <c>UseX(string connectionString)</c>
    /// overload sets. Because the callback captures the transport instance, it works for named brokers too.
    /// </para>
    /// </summary>
    /// <param name="kind">What needs it, for the message -- e.g. "Azure Service Bus".</param>
    /// <param name="connectionStringName">The connection string key.</param>
    /// <param name="aspireResourceMethod">The Aspire AppHost builder method that registers this resource.</param>
    /// <param name="apply">Receives the resolved, non-empty connection string value.</param>
    public static WolverineOptions ApplyNamedConnectionString(this WolverineOptions options, string kind,
        string connectionStringName, string? aspireResourceMethod, Action<string> apply)
    {
        return options.ApplyNamedConnectionString(
            new NamedConfigurationDependency(kind, connectionStringName, aspireResourceMethod), apply);
    }

    /// <summary>
    /// GH-5127. <see cref="ApplyNamedConnectionString(WolverineOptions,string,string,string?,Action{string})"/>
    /// for a fully described dependency, e.g. one whose <see cref="NamedConfigurationDependency.Usage"/> is
    /// "message store" rather than "transport".
    /// </summary>
    public static WolverineOptions ApplyNamedConnectionString(this WolverineOptions options,
        NamedConfigurationDependency dependency, Action<string> apply)
    {
        ArgumentNullException.ThrowIfNull(dependency);
        ArgumentNullException.ThrowIfNull(apply);

        if (!options.NamedConfigurationDependencies.Contains(dependency))
        {
            options.NamedConfigurationDependencies.Add(dependency);
        }

        options.NamedConnectionStringBindings.Add(new NamedConnectionStringBinding(dependency, apply));

        return options;
    }

    /// <summary>
    /// GH-5127. Make sure every connection string registered through <c>ApplyNamedConnectionString</c> has been
    /// resolved and applied. Wolverine does this itself at the top of startup, on first access to the message
    /// stores, and in the <c>resources</c> commands; a message store or transport that can be built through some
    /// other path before any of those (a DI factory resolved directly, say) calls this first. Idempotent and
    /// thread safe. Throws <see cref="MissingNamedConnectionStringsException"/> when any name is missing.
    /// </summary>
    public static void EnsureNamedConnectionStringsApplied(this IWolverineRuntime runtime)
    {
        if (runtime is WolverineRuntime wolverineRuntime)
        {
            wolverineRuntime.ApplyNamedConnectionStrings();
        }
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
