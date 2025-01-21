# Externally Controlled Database Tables <Badge type="tip" text="3.5" />

Let's say that you'd like to publish messages to a Wolverine application from an existing system where it's not feasible
to either utilize Wolverine, and that system does not currently have any kind of messaging capability. And of course, you
want the messaging to Wolverine to be robust through some sort of transactional outbox, but you certainly don't want to 
have to build custom infrastructure to manage that. 

Wolverine provides a capability to scrape an externally controlled database table for incoming messages in a reliable way.
Assuming that you are using one of the relational database options for persisting messages already like [PostgreSQL](/guide/durability/postgresql) 
or [Sql Server](/guide/durability/sqlserver), you can tell Wolverine to poll a table *in the same database as the message 
store* for incoming messages like this:

<!-- snippet: sample_configuring_external_database_messaging -->
<a id='snippet-sample_configuring_external_database_messaging'></a>
```cs
var builder = Host.CreateApplicationBuilder();
builder.UseWolverine(opts =>
{
    opts.UsePostgresqlPersistenceAndTransport(builder.Configuration.GetConnectionString("postgres"));

    // Or
    // opts.UseSqlServerPersistenceAndTransport(builder.Configuration.GetConnectionString("sqlserver"));
    
    // Or
    // opts.Services
    //     .AddMarten(builder.Configuration.GetConnectionString("postgres"))
    //     .IntegrateWithWolverine();
    
    // Directing Wolverine to "listen" for messages in an externally controlled table
    // You have to explicitly tell Wolverine about the schema name and table name
    opts.ListenForMessagesFromExternalDatabaseTable("exports", "messaging", table =>
        {
            // The primary key column for this table, default is "id"
            table.IdColumnName = "id";

            // What column has the actual JSON data? Default is "json"
            table.JsonBodyColumnName = "body";

            // Optionally tell Wolverine that the message type name is this
            // column. 
            table.MessageTypeColumnName = "message_type";

            // Add a column for the current time when a message was inserted
            // Strictly for diagnostics
            table.TimestampColumnName = "added";

            // How often should Wolverine poll this table? Default is 10 seconds
            table.PollingInterval = 1.Seconds();

            // Maximum number of messages that each node should try to pull in at 
            // any one time. Default is 100
            table.MessageBatchSize = 50;

            // Is Wolverine allowed to try to apply automatic database migrations for this
            // table at startup time? Default is true.
            // Also overridden by WolverineOptions.AutoBuildMessageStorageOnStartup
            table.AllowWolverineControl = true;

            // Wolverine uses a database advisory lock so that only one node at a time
            // can ever be polling for messages at any one time. Default is 12000
            // It might release contention to vary the advisory lock if you have multiple
            // incoming tables or applications targeting the same database
            table.AdvisoryLock = 12001;
            
            // Tell Wolverine what the default message type is coming from this
            // table to aid in deserialization
            table.MessageType = typeof(ExternalMessage);
            
            
        })
        
        // Just showing that you have all the normal options for configuring and
        // fine tuning the behavior of a message listening endpoint here
        .Sequential();
});
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Persistence/PostgresqlTests/Transport/external_message_tables.cs#L232-L295' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_configuring_external_database_messaging' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

So a couple things to know:

* The external table has to have a single primary key table that uses `Guid` as the .NET type. So `uuid` for PostgreSQL or 
  `uniqueidentifier` for Sql Server
* There must be a single column that holds the incoming message as JSON. For Sql Server this is `varbinary(max)` and `JSONB` for PostgreSQL
* If there is a column mapped for the message type, Wolverine is using its message type naming to determine the actual .NET
  message type. See [Message Type Name or Alias](/guide/messages.html#message-type-name-or-alias) for information about how to use this
  or even add custom type mapping to synchronize between the upstream system and your Wolverine using system
* If the upstream system is not sending a message type name, you will be limited to only accepting a single message type, and you will
  have to tell Wolverine the default message type as shown above in the code sample. This is common in interop with non-Wolverine systems
* All "external table" endpoints in Wolverine are "durable" endpoints, and the incoming messages get moved to the incoming envelope
  tables
* Likewise, the dead letter queueing for these messages is done with the typical database message store
