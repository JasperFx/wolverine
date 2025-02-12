# Dead Letter Storage

If [message storage](/guide/durability/) is configured for your application, and you're using either the local queues or messaging
transports where Wolverine doesn't (yet) support native [dead letter queueing](https://en.wikipedia.org/wiki/Dead_letter_queue), Wolverine is actually moving messages
to the `wolverine_dead_letters` table in your database in lieu of native dead letter queueing. 

You can browse the messages in this table and see some of the exception details that led them to being moved
to the dead letter queue. To recover messages from the dead letter queue after possibly fixing a production support
issue, you can update this table's `replayable` column for any messages you want to recover with some kind of
SQL command like:

```sql
update wolverine_dead_letters set replayable = true where exception_type = 'InvalidAccountException';
```

When you do this, Wolverine's durability agent that manages the inbox and outbox processing in the background
will move these messages back into active incoming message handling. Just note that this process happens
through some polling, so it won't be instantaneous.

To replay dead lettered messages back to the incoming table, you also have a command line option:

```bash
dotnet run -- storage replay
```

## Dead Letter Expiration <Badge type="tip" text="3.9" />

::: tip
You could see poor performance over time if the dead letter queue storage in the database gets excessively large,
so Wolverine does have an "opt in" feature to let old messages expire and be expunged from the storage.
:::

It's off by default (for backwards compatibility), but you can enable Wolverine to assign expiration times to dead letter
queue messages persisted to durable storage like this:

<!-- snippet: sample_enabling_dead_letter_queue_expiration -->
<a id='snippet-sample_enabling_dead_letter_queue_expiration'></a>
```cs
using var host = await Host.CreateDefaultBuilder()
    .UseWolverine(opts =>
    {

        // This is required
        opts.Durability.DeadLetterQueueExpirationEnabled = true;

        // Default is 10 days. This is the retention period
        opts.Durability.DeadLetterQueueExpiration = 3.Days();

    }).StartAsync();
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Testing/CoreTests/BootstrappingSamples.cs#L41-L55' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_enabling_dead_letter_queue_expiration' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

Note that Wolverine will use the message's `DeliverBy` value as the expiration if that exists, otherwise, Wolverine will
just add the `DeadLetterQueueExpiration` time to the current time. The actual stored messages are deleted by background
processes and won't be quite real time.

## Integrating Dead Letters REST API into Your Application

Integrating the Dead Letters REST API into your WolverineFX application provides an elegant and powerful way to manage dead letter messages directly through HTTP requests. This capability is crucial for applications that require a robust mechanism for dealing with message processing failures, enabling developers and administrators to query, replay, or delete dead letter messages as needed. Below, we detail how to add this functionality to your application and describe the usage of each endpoint.

To get started, install that Nuget reference:

```bash
dotnet add package WolverineFx.Http
```

### Adding Dead Letters REST API to Your Application

To integrate the Dead Letters REST API into your WolverineFX application, you simply need to register the endpoints in your application's startup process. This is done by calling `app.MapDeadLettersEndpoints();` within the `Configure` method of your `Startup` class or the application initialization block if using minimal API patterns. This method call adds the necessary routes and handlers for dead letter management to your application's routing table.

<!-- snippet: sample_register_dead_letter_endpoints -->
<a id='snippet-sample_register_dead_letter_endpoints'></a>
```cs
app.MapDeadLettersEndpoints()

    // It's a Minimal API endpoint group,
    // so you can add whatever authorization
    // or OpenAPI metadata configuration you need
    // for just these endpoints
    //.RequireAuthorization("Admin")

    ;
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Http/WolverineWebApi/Program.cs#L152-L162' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_register_dead_letter_endpoints' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

### Using the Dead Letters REST API

#### Query Dead Letters Endpoint

- **Path**: `/dead-letters/`
- **Method**: `POST`
- **Request Body**: `DeadLetterEnvelopeGetRequest`
  - `Limit` (uint): Number of records to return per page.
  - `StartId` (Guid?): Start fetching records after the specified ID.
  - `MessageType` (string?): Filter by message type.
  - `ExceptionType` (string?): Filter by exception type.
  - `ExceptionMessage` (string?): Filter by exception message.
  - `From` (DateTimeOffset?): Start date for fetching records.
  - `Until` (DateTimeOffset?): End date for fetching records.
  - `TenantId` (string?): Tenant identifier for multi-tenancy support.
- **Response**: `DeadLetterEnvelopesFoundResponse` containing a list of `DeadLetterEnvelopeResponse` objects and an optional `NextId` for pagination.

**Request Example**:

```json
{
  "Limit": 50,
  "MessageType": "OrderPlacedEvent",
  "ExceptionType": "InvalidOrderException"
}
```

**Reponse Example**:

```json
{
  "Messages": [
    {
      "Id": "4e3d5e88-e01f-4bcb-af25-6e4c14b0a867",
      "ExecutionTime": "2024-04-06T12:00:00Z",
      "Body": {
        "OrderId": 123456,
        "OrderStatus": "Failed",
        "Reason": "Invalid Payment Method"
      },
      "MessageType": "OrderFailedEvent",
      "ReceivedAt": "2024-04-06T12:05:00Z",
      "Source": "OrderService",
      "ExceptionType": "PaymentException",
      "ExceptionMessage": "The payment method provided is invalid.",
      "SentAt": "2024-04-06T12:00:00Z",
      "Replayable": true
    },
    {
      "Id": "5f2c3d1e-3f3d-46f9-ba29-dac8e0f9b078",
      "ExecutionTime": null,
      "Body": {
        "CustomerId": 78910,
        "AccountBalance": -150.75
      },
      "MessageType": "AccountOverdrawnEvent",
      "ReceivedAt": "2024-04-06T15:20:00Z",
      "Source": "AccountService",
      "ExceptionType": "OverdrawnException",
      "ExceptionMessage": "Account balance cannot be negative.",
      "SentAt": "2024-04-06T15:15:00Z",
      "Replayable": false
    }
  ],
  "NextId": "8a1d77f2-f91b-4edb-8b51-466b5a8a3a6f"
}
```

#### Replay Dead Letters Endpoint

- **Path**: `/dead-letters/replay`
- **Method**: `POST`
- **Description**: Marks specified dead letter messages as replayable. This operation signals the system to attempt reprocessing the messages, ideally after the cause of the initial failure has been resolved.

**Request Example**:

```json
{
  "Ids": ["d3b07384-d113-4ec8-98c4-b3bf34e2c572", "d3b07384-d113-4ec8-98c4-b3bf34e2c573"]
}
```

#### Delete Dead Letters Endpoint

- **Path**: `/dead-letters/`
- **Method**: `DELETE`
- **Description**: Permanently removes specified dead letter messages from the system. Use this operation to clear messages that are no longer needed or cannot be successfully reprocessed.

**Request Example**:

```json
{
  "Ids": ["d3b07384-d113-4ec8-98c4-b3bf34e2c574", "d3b07384-d113-4ec8-98c4-b3bf34e2c575"]
}
```

### Conclusion

By integrating the Dead Letters REST API into your WolverineFX application, you gain fine-grained control over the management of dead letter messages. This feature not only aids in debugging and resolving processing issues but also enhances the overall reliability of your message-driven applications.
