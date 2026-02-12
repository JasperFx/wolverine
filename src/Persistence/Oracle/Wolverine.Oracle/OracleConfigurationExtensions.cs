using JasperFx.Core;
using JasperFx.Core.Reflection;
using Wolverine.Configuration;
using Wolverine.Oracle.Transport;
using Wolverine.Persistence.Durability;

namespace Wolverine.Oracle;

public static class OracleConfigurationExtensions
{
    /// <summary>
    ///     Register Oracle backed message persistence to a known connection string
    /// </summary>
    /// <param name="options"></param>
    /// <param name="connectionString"></param>
    /// <param name="schemaName">Optional schema name for the Wolverine envelope storage</param>
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
