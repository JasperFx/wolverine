namespace Wolverine.Transports.Postgresql.Internal;

/// <summary>
/// Describes a queue in the database 
/// </summary>
public sealed class QueueDefinition : DatabaseObjectDefinition
{
    /// <summary>
    /// Initializes a new instance of the QueueDefinition class with the given queue name.
    /// </summary>
    /// <param name="name">The name of the queue.</param>
    public QueueDefinition(string name)
    {
        Name = NamingHelpers.SanitizeQueueName(name);
        QueueName = NamingHelpers.GetQueueName(Name);
        ChannelName = NamingHelpers.GetQueueChannelName(Name);
        TriggerName = NamingHelpers.GetTriggerName(Name);
        TriggerFunctionName = NamingHelpers.GetTriggerFunctionName(Name);
        Uri = new Uri($"{PostgresTransport.ProtocolName}://queue/{Name}");
    }

    /// <summary>
    /// Gets the name of the queue.
    /// </summary>
    public string Name { get; }

    /// <summary>
    /// Gets the URI of the queue.
    /// </summary>
    public Uri Uri { get; }

    /// <summary>
    /// Gets the name of the queue table.
    /// </summary>
    public string QueueName { get; }

    /// <summary>
    /// Get the name of the channel that is notified when new queue items are defined
    /// </summary>
    public string ChannelName { get; }

    /// <summary>
    /// Get the name of the trigger that is triggered when new queue items are added   
    /// </summary>
    public string TriggerName { get; }

    /// <summary>
    /// Get the name of the trigger function that is invoked by the trigger 
    /// </summary>
    public string TriggerFunctionName { get; }

    /// <inheritdoc />
    protected override string BuildCreateStatement()
    {
        return $"""
            CREATE TABLE IF NOT EXISTS {QueueName}
            (
                Id UUID NOT NULL,
                SenderId UUID NOT NULL,
                CorrelationId VARCHAR(256),
                MessageType VARCHAR(256),
                ContentType VARCHAR(256),
                ScheduledTime TIMESTAMP WITH TIME ZONE,
                Data BYTEA,
                Headers jsonb,
                Attempts INT,
                PRIMARY KEY (Id)
            );

            --- create a index for the scheduled time to speed up the query
            CREATE INDEX IF NOT EXISTS {QueueName}_scheduled_time_idx ON {QueueName} (ScheduledTime);
            
            --- create a index for the sender id to speed up the query
            CREATE INDEX IF NOT EXISTS {QueueName}_sender_id_idx ON {QueueName} (SenderId);
            
            CREATE OR REPLACE FUNCTION {TriggerFunctionName}()
              RETURNS trigger AS
            $BODY$
            DECLARE
            BEGIN
              PERFORM pg_notify('{ChannelName}', NEW.Id::TEXT);
              RETURN NEW;
            END;
            $BODY$
              LANGUAGE plpgsql VOLATILE; --- VOLATILE indicates that the result of the function may change from one call to the next because of pg_notify

            CREATE OR REPLACE TRIGGER {TriggerName}
            AFTER INSERT OR UPDATE ON {QueueName}
            FOR EACH ROW
            EXECUTE PROCEDURE {TriggerFunctionName}();
        """;
    }

    /// <inheritdoc />
    protected override string BuildDropStatement()
    {
        return $"""
            DROP TABLE IF EXISTS {QueueName};
            DROP TRIGGER IF EXISTS {TriggerName} ON {QueueName};
            DROP FUNCTION IF EXISTS {TriggerFunctionName}();
        """;
    }

    /// <inheritdoc />
    protected override string BuildExistsStatement()
    {
        return $"""
            SELECT EXISTS (
                SELECT 1
                FROM   information_schema.tables 
                WHERE  table_name = '{QueueName}'
            );
        """;
    }
}
