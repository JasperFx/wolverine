using JasperFx;
using JasperFx.Core;
using JasperFx.Core.Reflection;
using JasperFx.Events;
using Polecat;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Wolverine.Polecat.Publishing;
using Wolverine.Persistence.Durability;
using Wolverine.SqlServer.Persistence;
using Wolverine.RDBMS;
using Wolverine.RDBMS.Sagas;
using Wolverine.Runtime;
using MultiTenantedMessageStore = Wolverine.Persistence.Durability.MultiTenantedMessageStore;

namespace Wolverine.Polecat;

internal class MapEventTypeMessages : IWolverineExtension
{
    public void Configure(WolverineOptions options)
    {
        options.MapGenericMessageType(typeof(IEvent<>), typeof(Event<>));
    }
}

public static class WolverineOptionsPolecatExtensions
{
    /// <summary>
    ///     Integrate Polecat with Wolverine's persistent outbox and add Polecat-specific middleware
    ///     to Wolverine
    /// </summary>
    public static PolecatConfigurationExpression IntegrateWithWolverine(
        this PolecatConfigurationExpression expression,
        Action<PolecatIntegration>? configure = null)
    {
        var integration = expression.Services.FindPolecatIntegration();
        if (integration == null)
        {
            integration = new PolecatIntegration();

            configure?.Invoke(integration);

            expression.Services.AddSingleton(integration);
            expression.Services.AddSingleton<IWolverineExtension>(integration);
        }
        else
        {
            configure?.Invoke(integration);
        }

        expression.Services.AddSingleton<IWolverineExtension, MapEventTypeMessages>();

        expression.Services.AddScoped<IPolecatOutbox, PolecatOutbox>();

        expression.Services.AddSingleton<DatabaseSettings>(s =>
        {
            var store = s.GetRequiredService<IMessageStore>() as IMessageDatabase;
            if (store != null) return store.Settings;

            return new DatabaseSettings();
        });

        expression.Services.AddSingleton<IMessageStore>(s =>
        {
            var store = s.GetRequiredService<IDocumentStore>();
            var runtime = s.GetRequiredService<IWolverineRuntime>();
            var logger = s.GetRequiredService<ILogger<SqlServerMessageStore>>();

            var schemaName = integration.MessageStorageSchemaName ??
                             runtime.Options.Durability.MessageStorageSchemaName ??
                             "wolverine";

            return BuildSqlServerMessageStore(schemaName, store, runtime, logger);
        });

        expression.Services.AddSingleton<IConfigurePolecat, PolecatOverrides>();

        expression.Services.AddSingleton<OutboxedSessionFactory>();

        return expression;
    }

    internal static IMessageStore BuildSqlServerMessageStore(
        string schemaName,
        IDocumentStore store,
        IWolverineRuntime runtime,
        ILogger<SqlServerMessageStore> logger)
    {
        var settings = new DatabaseSettings
        {
            SchemaName = schemaName,
            AutoCreate = AutoCreate.CreateOrUpdate,
            Role = MessageStoreRole.Main,
            ScheduledJobLockId = $"{schemaName}:scheduled-jobs".GetDeterministicHashCode(),
            ConnectionString = store.Options.ConnectionString
        };

        var sagaTypes = runtime.Services.GetServices<SagaTableDefinition>();
        return new SqlServerMessageStore(settings, runtime.Options.Durability, logger, sagaTypes);
    }

    internal static PolecatIntegration? FindPolecatIntegration(this IServiceCollection services)
    {
        var descriptor = services.FirstOrDefault(x =>
            x.ServiceType == typeof(IWolverineExtension) && x.ImplementationInstance is PolecatIntegration);

        return descriptor?.ImplementationInstance as PolecatIntegration;
    }
}
