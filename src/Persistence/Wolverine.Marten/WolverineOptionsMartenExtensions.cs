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
using JasperFx.Core;
using Lamar;
using Marten.Storage;
using Npgsql;
using Weasel.Postgresql;

namespace Wolverine.Marten;

public static class WolverineOptionsMartenExtensions
{
    /// <summary>
    ///     Integrate Marten with Wolverine's persistent outbox and add Marten-specific middleware
    ///     to Wolverine
    /// </summary>
    /// <param name="expression"></param>
    /// <param name="schemaName">Optionally move the Wolverine envelope storage to a separate schema</param>
    /// <param name="masterDataSource">
    ///     In the case of Marten using a database per tenant, you may wish to
    ///     explicitly determine the master database for Wolverine where Wolverine will store node and envelope information.
    ///     This does not have to be one of the tenant databases
    ///     Wolverine will try to use the master database from the Marten configuration when possible
    /// </param>
    /// <param name="masterDatabaseConnectionString">
    ///     In the case of Marten using a database per tenant, you may wish to
    ///     explicitly determine the master database for Wolverine where Wolverine will store node and envelope information.
    ///     This does not have to be one of the tenant databases
    ///     Wolverine will try to use the master database from the Marten configuration when possible
    /// </param>
    /// <returns></returns>
    public static MartenServiceCollectionExtensions.MartenConfigurationExpression IntegrateWithWolverine(
        this MartenServiceCollectionExtensions.MartenConfigurationExpression expression, string? schemaName = null,
        string? masterDatabaseConnectionString = null, NpgsqlDataSource? masterDataSource = null)
    {
        if (schemaName.IsNotEmpty() && schemaName != schemaName.ToLowerInvariant())
        {
            throw new ArgumentOutOfRangeException(nameof(schemaName),
                "The schema name must be in all lower case characters");
        }
        
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

            return BuildMultiTenantedMessageDatabase(schemaName, masterDatabaseConnectionString, masterDataSource, store, runtime, s);
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

        public ValueTask<IReadOnlyList<IDatabase>> BuildDatabases()
        {
            if (_runtime.Storage is PostgresqlMessageStore database)
                return new ValueTask<IReadOnlyList<IDatabase>>(new List<IDatabase>{database});

            if (_runtime.Storage is MultiTenantedMessageDatabase tenants)
            {
                tenants.Initialize(_runtime);
                return new ValueTask<IReadOnlyList<IDatabase>>(tenants.AllDatabases());
            }

            return new ValueTask<IReadOnlyList<IDatabase>>(Array.Empty<IDatabase>());
        }
    }

    internal static NpgsqlDataSource findMasterDataSource(DocumentStore store, IWolverineRuntime runtime,
        DatabaseSettings masterSettings, IServiceProvider container)
    {
        if (store.Tenancy is ITenancyWithMasterDatabase m) return m.TenantDatabase.DataSource;

        if (masterSettings.DataSource != null) return (NpgsqlDataSource)masterSettings.DataSource;
        
        if (masterSettings.ConnectionString.IsNotEmpty()) return NpgsqlDataSource.Create(masterSettings.ConnectionString);

        var source = container.GetService<NpgsqlDataSource>();

        return source ??
               throw new InvalidOperationException(
                   "There is no configured connectivity for the required master PostgreSQL message database");
    }

    internal static IMessageStore BuildMultiTenantedMessageDatabase(string schemaName,
        string? masterDatabaseConnectionString, NpgsqlDataSource? masterDataSource, DocumentStore store,
        IWolverineRuntime runtime,
        IServiceProvider serviceProvider)
    {
        var masterSettings = new DatabaseSettings
        {
            ConnectionString = masterDatabaseConnectionString,
            SchemaName = schemaName,
            IsMaster = true,
            CommandQueuesEnabled = true,
            DataSource = masterDataSource
        };

        var dataSource = findMasterDataSource(store, runtime, masterSettings, serviceProvider);
        var master = new PostgresqlMessageStore(masterSettings, runtime.Options.Durability, dataSource,
            runtime.LoggerFactory.CreateLogger<PostgresqlMessageStore>())
        {
            Name = "Master"
        };


        var source = new MartenMessageDatabaseSource(schemaName, store, runtime);
        
        master.Initialize(runtime);

        return new MultiTenantedMessageDatabase(master, runtime, source);
    }

    internal static IMessageStore BuildSinglePostgresqlMessageStore(string schemaName, DocumentStore store,
        IWolverineRuntime runtime, ILogger<PostgresqlMessageStore> logger)
    {
        var settings = new DatabaseSettings
        {
            SchemaName = schemaName,
            IsMaster = true,
            ScheduledJobLockId = $"{schemaName ?? "public"}:scheduled-jobs".GetDeterministicHashCode()
        };

        var dataSource = store.Storage.Database.As<PostgresqlDatabase>().DataSource;

        return new PostgresqlMessageStore(settings, runtime.Options.Durability, dataSource, logger);
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
    
    /// <summary>
    ///     Enable publishing of events to Wolverine message routing when captured in Marten sessions that are enrolled in a
    ///     Wolverine outbox
    /// </summary>
    /// <param name="expression"></param>
    /// <returns></returns>
    public static MartenServiceCollectionExtensions.MartenConfigurationExpression EventForwardingToWolverine(
        this MartenServiceCollectionExtensions.MartenConfigurationExpression expression, Action<IEventForwarding> configure)
    {
        var integration = expression.Services.FindMartenIntegration();
        if (integration == null)
        {
            expression.IntegrateWithWolverine();
            integration = expression.Services.FindMartenIntegration();
        }

        integration!.ShouldPublishEvents = true;

        configure(integration);

        return expression;
    }
}

