using JasperFx.Core;
using JasperFx.Core.Reflection;
using JasperFx.MultiTenancy;
using Wolverine.Configuration;
using Wolverine.Oracle.Transport;
using Wolverine.Persistence.Durability;
using Wolverine.RDBMS.MultiTenancy;

namespace Wolverine.Oracle;

public static class OracleConfigurationExtensions
{
    /// <summary>
    ///     Register Oracle backed message persistence to a known connection string
    /// </summary>
    /// <param name="options"></param>
    /// <param name="connectionString"></param>
    /// <param name="schemaName">Optional schema name for the Wolverine envelope storage. This has to be a single database identifier; a multi-part name like "crm.sales" is rejected. See GH-3997</param>
    /// <param name="role">Default is Main. Use this to mark some stores as Ancillary to disambiguate the main storage for Wolverine</param>
    public static IOracleBackedPersistence PersistMessagesWithOracle(this WolverineOptions options,
        string connectionString,
        string? schemaName = null, MessageStoreRole role = MessageStoreRole.Main)
    {
        var persistence = new OracleBackedPersistence(options.Durability, options)
        {
            ConnectionString = connectionString,
            AlreadyIncluded = true,
            Role = role
        };

        if (schemaName.IsNotEmpty())
        {
            persistence.EnvelopeStorageSchemaName = schemaName.ToUpperInvariant();
        }

        persistence.Configure(options);

        return persistence;
    }

    internal static NamedConfigurationDependency NamedConnectionDependency(string connectionStringName) =>
        new("Oracle", connectionStringName, "AddOracle(\"oracle\").AddDatabase") { Usage = "message store" };

    /// <summary>
    ///     Register Oracle backed message persistence to the named connection string from IConfiguration. This
    ///     option is useful for Aspire integration, where the AppHost's <c>builder.AddOracle("server").AddDatabase("name")</c>
    ///     plus <c>.WithReference(...)</c> supplies <c>ConnectionStrings:name</c>. The value is resolved once the
    ///     application's container exists, before the message store is built; a missing name fails startup together
    ///     with every other missing named connection string.
    /// </summary>
    /// <param name="options"></param>
    /// <param name="connectionStringName">Key of the expected connection string from IConfiguration</param>
    /// <param name="schemaName">Optional schema name for the Wolverine envelope storage. This has to be a single database identifier; a multi-part name like "crm.sales" is rejected. See GH-3997</param>
    /// <param name="role">Default is Main. Use this to mark some stores as Ancillary to disambiguate the main storage for Wolverine</param>
    public static IOracleBackedPersistence PersistMessagesWithOracleUsingNamedConnection(this WolverineOptions options,
        string connectionStringName, string? schemaName = null, MessageStoreRole role = MessageStoreRole.Main)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionStringName);

        var dependency = NamedConnectionDependency(connectionStringName);
        var persistence = new OracleBackedPersistence(options.Durability, options)
        {
            NamedConnection = dependency,
            AlreadyIncluded = true,
            Role = role
        };

        if (schemaName.IsNotEmpty())
        {
            persistence.EnvelopeStorageSchemaName = schemaName.ToUpperInvariant();
        }

        options.ApplyNamedConnectionString(dependency, value => persistence.ConnectionString = value);

        persistence.Configure(options);

        return persistence;
    }

    /// <summary>
    ///     Opt into static per-tenant database multi-tenancy where each tenant database's connection string is
    ///     resolved by name from IConfiguration -- e.g. one Aspire database resource per tenant, each referenced
    ///     from this project. Every name joins the aggregated startup check.
    /// </summary>
    /// <param name="persistence"></param>
    /// <param name="configure">Register each tenant id and the name of its connection string</param>
    public static IOracleBackedPersistence RegisterStaticTenantsUsingNamedConnections(
        this IOracleBackedPersistence persistence, Action<NamedConnectionStringTenants> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);

        var tenants = new NamedConnectionStringTenants();
        configure(tenants);

        StaticConnectionStringSource source = null!;
        persistence.RegisterStaticTenants(x => source = x);

        tenants.ApplyTo(((OracleBackedPersistence)persistence).Options, source, "Oracle", "AddOracle(\"oracle\").AddDatabase");

        return persistence;
    }

    /// <summary>
    ///     Quick access to the Oracle Transport within this application.
    ///     This is for advanced usage
    /// </summary>
    internal static OracleTransport OracleTransport(this WolverineOptions endpoints)
    {
        var transports = endpoints.As<WolverineOptions>().Transports;

        try
        {
            return transports.GetOrCreate<OracleTransport>();
        }
        catch (Exception)
        {
            throw new InvalidOperationException("The Oracle transport is not registered in this system");
        }
    }

    /// <summary>
    /// Listen for incoming messages at the designated Oracle queue by name
    /// </summary>
    public static OracleListenerConfiguration ListenToOracleQueue(this WolverineOptions endpoints, string queueName)
    {
        var transport = endpoints.OracleTransport();
        var corrected = transport.MaybeCorrectName(queueName);
        var queue = transport.Queues[corrected];
        queue.EndpointName = queueName;
        queue.IsListener = true;

        return new OracleListenerConfiguration(queue);
    }

    /// <summary>
    ///     Publish matching messages straight to an Oracle queue using the queue name
    /// </summary>
    public static OracleSubscriberConfiguration ToOracleQueue(this IPublishToExpression publishing,
        string queueName)
    {
        var transports = publishing.As<PublishingExpression>().Parent.Transports;
        var transport = transports.GetOrCreate<OracleTransport>();

        var corrected = transport.MaybeCorrectName(queueName);
        var queue = transport.Queues[corrected];
        queue.EndpointName = queueName;

        // This is necessary unfortunately to hook up the subscription rules
        publishing.To(queue.Uri);

        return new OracleSubscriberConfiguration(queue);
    }
}
