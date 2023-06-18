using JasperFx.Core.Reflection;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Weasel.Core.Migrations;
using Wolverine.Marten.Publishing;
using Wolverine.Persistence.Durability;
using Wolverine.Postgresql;
using Wolverine.RDBMS;
using Wolverine.RDBMS.MultiTenancy;
using Wolverine.Runtime;

namespace Wolverine.Marten;

public static class WolverineOptionsMartenExtensions
{
    /// <summary>
    ///     Integrate Marten with Wolverine's persistent outbox and add Marten-specific middleware
    ///     to Wolverine
    /// </summary>
    /// <param name="expression"></param>
    /// <param name="schemaName">Optionally move the Wolverine envelope storage to a separate schema</param>
    /// <param name="masterDatabaseConnectionString">
    ///     In the case of Marten using a database per tenant, you may wish to
    ///     explicitly determine the master database for Wolverine where Wolverine will store node and envelope information.
    ///     This does not have to be one of the tenant databases
    /// </param>
    /// <returns></returns>
    public static MartenServiceCollectionExtensions.MartenConfigurationExpression IntegrateWithWolverine(
        this MartenServiceCollectionExtensions.MartenConfigurationExpression expression, string? schemaName = null,
        string? masterDatabaseConnectionString = null)
    {
        expression.Services.AddScoped<IMartenOutbox, MartenOutbox>();

        expression.Services.AddSingleton<IMessageStore>(s =>
        {
            var store = s.GetRequiredService<IDocumentStore>().As<DocumentStore>();

            var runtime = s.GetRequiredService<IWolverineRuntime>();
            var logger = s.GetRequiredService<ILogger<PostgresqlMessageStore>>();

            schemaName ??= store.Options.DatabaseSchemaName;

            // TODO -- hacky. Need a way to expose this in Marten
            if (store.Tenancy.GetType().Name == "DefaultTenancy")
            {
                return BuildSinglePostgresqlMessageStore(schemaName, store, runtime, logger);
            }

            return BuildMultiTenantedMessageDatabase(schemaName, masterDatabaseConnectionString, store, runtime);
        });

        expression.Services.AddSingleton<IDatabaseSource, MartenMessageDatabaseDiscovery>();

        expression.Services.AddSingleton<IWolverineExtension>(new MartenIntegration());
        expression.Services.AddSingleton<OutboxedSessionFactory>();

        return expression;
    }

    internal class MartenMessageDatabaseDiscovery : IDatabaseSource
    {
        private readonly IWolverineRuntime _runtime;

        public MartenMessageDatabaseDiscovery(IWolverineRuntime runtime)
        {
            _runtime = runtime;
        }

        public async ValueTask<IReadOnlyList<IDatabase>> BuildDatabases()
        {
            if (_runtime.Storage is PostgresqlMessageStore database)
                return new List<IDatabase>{database};

            if (_runtime.Storage is MultiTenantedMessageDatabase tenants)
            {
                await tenants.InitializeAsync(_runtime);
                return tenants.AllDatabases();
            }

            return Array.Empty<IDatabase>();
        }
    }

    internal static IMessageStore BuildMultiTenantedMessageDatabase(string schemaName,
        string? masterDatabaseConnectionString, DocumentStore store, IWolverineRuntime runtime)
    {
        if (masterDatabaseConnectionString.IsEmpty())
        {
            throw new ArgumentOutOfRangeException(nameof(masterDatabaseConnectionString),
                "Must specify a master Wolverine database connection string in the case of using Marten multi-tenancy with multiple databases");
        }

        var masterSettings = new DatabaseSettings
        {
            ConnectionString = masterDatabaseConnectionString,
            SchemaName = schemaName,
            IsMaster = true,
            CommandQueuesEnabled = true
        };

        var source = new MartenMessageDatabaseSource(schemaName, store, runtime);
        var master = new PostgresqlMessageStore(masterSettings, runtime.Options.Durability,
            runtime.LoggerFactory.CreateLogger<PostgresqlMessageStore>())
        {
            Name = "Master"
        };

        return new MultiTenantedMessageDatabase(master, runtime, source);
    }

    internal static IMessageStore BuildSinglePostgresqlMessageStore(string schemaName, DocumentStore store,
        IWolverineRuntime runtime, ILogger<PostgresqlMessageStore> logger)
    {
        var martenDatabase = store.Storage.Database;

        var settings = new DatabaseSettings
        {
            ConnectionString = martenDatabase.CreateConnection().ConnectionString,
            SchemaName = schemaName,
            IsMaster = true
        };

        return new PostgresqlMessageStore(settings, runtime.Options.Durability, logger);
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