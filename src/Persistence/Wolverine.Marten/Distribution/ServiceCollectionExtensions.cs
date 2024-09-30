using Marten;
using Marten.Events.Daemon.Coordination;
using Microsoft.Extensions.DependencyInjection;
using Wolverine.Runtime.Agents;

namespace Wolverine.Marten.Distribution;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Apply the Critter Stack Pro projection distribution to manage the execution of Marten
    /// asynchronous projections. You will need to separately call AddMarten().IntegrateWithWolverine()
    /// for this to be applicable
    /// </summary>
    /// <param name="services"></param>
    /// <returns></returns>
    public static IServiceCollection AddCritterStackProProjectionDistribution(this IServiceCollection services)
    {
        services.AddSingleton<IAgentFamily, ProjectionAgents>();
        services.AddSingleton<IProjectionCoordinator, ProjectionCoordinator>();

        return services;
    }

    /// <summary>
    /// Integrate with Wolverine for envelope storage (inbox/outbox), Wolverine middleware, and the "Critter Stack Pro" library for distributing asynchronous projection work
    /// </summary>
    /// <param name="expression"></param>
    /// <param name="schemaName">Optional PostgreSQL schema name to hold Wolverine envelope storage</param>
    /// <param name="masterDatabaseConnectionString">
    ///     In the case of Marten using a database per tenant, you may wish to
    ///     explicitly determine the master database for Wolverine where Wolverine will store node and envelope information.
    ///     This does not have to be one of the tenant databases
    /// </param>
    /// <returns></returns>
    public static MartenServiceCollectionExtensions.MartenConfigurationExpression IntegrateWithCritterStackPro(this MartenServiceCollectionExtensions.MartenConfigurationExpression expression, string? schemaName = null,
        string? masterDatabaseConnectionString = null)
    {
        expression.IntegrateWithWolverine(schemaName, masterDatabaseConnectionString);
        expression.Services.AddCritterStackProProjectionDistribution();
        return expression;
    }
}