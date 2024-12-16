namespace Wolverine.RDBMS.Transport;

public interface IExternalMessageTable
{
    TimeSpan PollingInterval { get; set; }
    
    /// <summary>
    /// What is the column name for the primary key of the external table? At this point, this
    /// must be a Guid value. Default is "id"
    /// </summary>
    string IdColumnName { get; set; }
    
    /// <summary>
    /// What is the column name for the column that holds the actual JSON data? Default is "json"
    /// </summary>
    string JsonBodyColumnName { get; set; }
    
    /// <summary>
    /// What is the column name for the message type name? Only use this if you expect to receive
    /// more than one type of message from this table. If null or empty, Wolverine will omit this column.
    /// Default is null. If not null though, make sure you use the MessageTypeMapping
    /// </summary>
    string MessageTypeColumnName { get; set; }

    /// <summary>
    /// What is the column name for an informational timestamp column? If it's null, Wolverine
    /// will omit this column. The default is "timestamp"
    /// </summary>
    string TimestampColumnName { get; set; }
    
    /// <summary>
    /// Should Wolverine try to automatically execute database migrations for this table
    /// on startup? Default is true. 
    /// </summary>
    bool AllowWolverineControl { get; set; }
    
    /// <summary>
    /// If you are not sending message type information in your external table, set this
    /// so that Wolverine "knows" what type the incoming message should be deserialized to
    /// </summary>
    Type? MessageType { get; set; }
    
    /// <summary>
    /// How many messages at a time should the application pull in from the database?
    /// Default is 100
    /// </summary>
    int MessageBatchSize { get; set; }
    
    /// <summary>
    /// Wolverine uses an advisory lock against the database so that only one node
    /// at a time can be pulling in messages from the external tables. The default
    /// is 12000. You may want to change this to prevent collisions between applications
    /// accessing the same database. 
    /// </summary>
    int AdvisoryLock { get; set; }
}