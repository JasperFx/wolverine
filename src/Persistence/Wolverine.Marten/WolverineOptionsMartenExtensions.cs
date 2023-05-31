using Marten;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Wolverine.Marten.Publishing;
using Wolverine.Persistence.Durability;
using Wolverine.Postgresql;
using Wolverine.RDBMS;

namespace Wolverine.Marten;

public static class WolverineOptionsMartenExtensions
{
    /// <summary>
    ///     Integrate Marten with Wolverine's persistent outbox and add Marten-specific middleware
    ///     to Wolverine
    /// </summary>
    /// <param name="expression"></param>
    /// <param name="schemaName">Optionally move the Wolverine envelope storage to a separate schema</param>
    /// <returns></returns>
    public static MartenServiceCollectionExtensions.MartenConfigurationExpression IntegrateWithWolverine(
        this MartenServiceCollectionExtensions.MartenConfigurationExpression expression, string? schemaName = null)
    {
        expression.Services.ConfigureMarten(opts =>
        {
            opts.Storage.Add(new MartenDatabaseSchemaFeature(schemaName ?? opts.DatabaseSchemaName));
        });

        expression.Services.AddScoped<IMartenOutbox, MartenOutbox>();

        expression.Services.AddSingleton<IMessageStore>(s =>
        {
            var store = s.GetRequiredService<IDocumentStore>();

            var settings = new DatabaseSettings
            {
                ConnectionString = store.Storage.Database.CreateConnection().ConnectionString,
                SchemaName = schemaName ?? store.Options.DatabaseSchemaName,
                IsMaster = true // TODO -- will change when we do multi-tenancy support
                
            };

            if (settings.SchemaName.IsEmpty()) settings.SchemaName = "public";

            var durability = s.GetRequiredService<DurabilitySettings>();
            var logger = s.GetRequiredService<ILogger<PostgresqlMessageStore>>();

            return new PostgresqlMessageStore(settings, durability, logger);
        });
        
        
        expression.Services.AddSingleton<IWolverineExtension>(new MartenIntegration());
        expression.Services.AddSingleton<OutboxedSessionFactory>();


        return expression;
    }

    internal static MartenIntegration? FindMartenIntegration(this IServiceCollection services)
    {
        var descriptor = services.FirstOrDefault(x =>
            x.ServiceType == typeof(IWolverineExtension) && x.ImplementationInstance is MartenIntegration);

        return descriptor?.ImplementationInstance as MartenIntegration;
    }

    /// <summary>
    ///     Enable publishing of events to Wolverine message routing when captured in Marten sessions that are enrolled in a
    ///     Wolverine outbox
    /// </summary>
    /// <param name="expression"></param>
    /// <returns></returns>
    public static MartenServiceCollectionExtensions.MartenConfigurationExpression EventForwardingToWolverine(
        this MartenServiceCollectionExtensions.MartenConfigurationExpression expression)
    {
        var integration = expression.Services.FindMartenIntegration();
        if (integration == null)
        {
            expression.IntegrateWithWolverine();
            integration = expression.Services.FindMartenIntegration();
        }

        integration!.ShouldPublishEvents = true;

        return expression;
    }
}