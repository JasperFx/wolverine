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

## Introspecting an Endpoint's Dead Letter Destination <Badge type="tip" text="6.9" />

Where an endpoint's dead letters actually go varies by transport and configuration: some endpoints
move failures to Wolverine's durable `wolverine_dead_letters` storage, while others use a **native
broker dead letter queue** (RabbitMQ DLX, an SQS dead letter queue, the Azure Service Bus
`$DeadLetterQueue`, etc.) that a tool managing the durable store can't see.

Every endpoint declares its effective destination through a single transport-agnostic enum,
`DeadLetterStorageMode`, so monitoring tools can introspect it without transport-specific knowledge:

| Value | Meaning |
|-------|---------|
| `Durable` | Dead letters go to Wolverine's durable store (`wolverine_dead_letters`) — queryable and replayable through `IDeadLetters`. |
| `Native` | Dead letters go to a native broker dead letter queue and are **not** bridged into durable storage. |
| `NativeWithRecovery` | Dead letters go to a native broker dead letter queue **and** are bridged back into durable storage via [`EnableDeadLetterQueueRecovery()`](/guide/messaging/transports/rabbitmq/deadletterqueues.html#recovering-native-dead-letters-to-durable-storage). |

It is exposed two ways:

- `Endpoint.DeadLetterStorage` on the endpoint model.
- `EndpointDescriptor.DeadLetterStorage` on the diagnostic descriptor surface that monitoring tools
  (for example [CritterWatch](https://github.com/JasperFx/CritterWatch)) read.

This lets a monitor detect endpoints that dead-letter **natively without recovery** (`Native`) and
recommend enabling recovery so those dead letters become visible and replayable in the durable store.

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
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Testing/CoreTests/BootstrappingSamples.cs#L40-L53' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_enabling_dead_letter_queue_expiration' title='Start of snippet'>anchor</a></sup>
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
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Http/WolverineWebApi/Program.cs#L288-L298' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_register_dead_letter_endpoints' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

### Using the Dead Letters REST API

#### Query Dead Letters Endpoint

- **Path**: `/dead-letters/`
- **Method**: `POST`
- **Request Body**: `DeadLetterEnvelopeGetRequest`
  - `Limit` (uint, default `100`): Number of records to return per page.
  - `PageNumber` (int): Page number for offset-based pagination — pass `0` (or omit) for the first page.
  - `MessageType` (string?): Filter by message type.
  - `ExceptionType` (string?): Filter by exception type.
  - `ExceptionMessage` (string?): Filter by exception message.
  - `From` (DateTimeOffset?): Start date for fetching records.
  - `Until` (DateTimeOffset?): End date for fetching records.
  - `TenantId` (string?): Tenant identifier for multi-tenancy support.
  - `DatabaseUri` (Uri?): Scope the query to a single message store when the application has multiple (ancillary) stores configured. Omit to query every store.
- **Response**: `IReadOnlyList<DeadLetterEnvelopeResults>` — one entry per matching message store. Each `DeadLetterEnvelopeResults` contains:
  - `TotalCount` (int): Total matching records for the filter in that store.
  - `Envelopes` (`List<DeadLetterEnvelope>`): The page of dead-letter envelopes.
  - `PageNumber` (int): Echo of the requested page number.
  - `DatabaseUri` (Uri?): The URI identifying which message store this result is from.

::: tip Pagination
The pagination model changed from cursor-based (`StartId` / `NextId`) to offset-based (`PageNumber`) in Wolverine 5. Pass `PageNumber` incrementing from `0` to walk through pages; `TotalCount` lets you compute how many pages exist as `ceil(TotalCount / Limit)`.
:::

**Request Example**:

```json
{
  "Limit": 50,
  "PageNumber": 0,
  "MessageType": "OrderPlacedEvent",
  "ExceptionType": "InvalidOrderException"
}
```

**Response Example** (one store; multi-store apps return additional array entries with their own `DatabaseUri`):

```json
[
  {
    "TotalCount": 247,
    "PageNumber": 0,
    "DatabaseUri": "postgresql://localhost:5432/orders",
    "Envelopes": [
      {
        "Id": "4e3d5e88-e01f-4bcb-af25-6e4c14b0a867",
        "ExecutionTime": "2026-04-06T12:00:00Z",
        "MessageType": "OrderFailedEvent",
        "ReceivedAt": "rabbitmq://exchange/orders",
        "Source": "OrderService",
        "ExceptionType": "PaymentException",
        "ExceptionMessage": "The payment method provided is invalid.",
        "SentAt": "2026-04-06T12:00:00Z",
        "Replayable": true,
        "Envelope": { /* the raw wire Envelope (headers, body, destination, etc.) */ },
        "Message": {
          "OrderId": 123456,
          "OrderStatus": "Failed",
          "Reason": "Invalid Payment Method"
        }
      },
      {
        "Id": "5f2c3d1e-3f3d-46f9-ba29-dac8e0f9b078",
        "ExecutionTime": null,
        "MessageType": "AccountOverdrawnEvent",
        "ReceivedAt": "rabbitmq://exchange/accounts",
        "Source": "AccountService",
        "ExceptionType": "OverdrawnException",
        "ExceptionMessage": "Account balance cannot be negative.",
        "SentAt": "2026-04-06T15:15:00Z",
        "Replayable": false,
        "Envelope": { /* … */ },
        "Message": {
          "CustomerId": 78910,
          "AccountBalance": -150.75
        }
      }
    ]
  }
]
```

The `Message` property is the deserialized message body — populated when Wolverine's handler graph knows the message type and a matching serializer is registered. The full wire `Envelope` (headers, content type, destination, etc.) is also returned for inspection.

#### Replay Dead Letters Endpoint

- **Path**: `/dead-letters/replay`
- **Method**: `POST`
- **Description**: Marks specified dead letter messages as replayable. This operation signals the system to attempt reprocessing the messages, ideally after the cause of the initial failure has been resolved.
- **Request Body**: `DeadLetterEnvelopeIdsRequest`
  - `Ids` (Guid[]): Identifiers of the dead-letter envelopes to replay.
  - `TenantId` (string?): If set, the replay is scoped to the tenant's message store.

**Request Example**:

```json
{
  "Ids": ["d3b07384-d113-4ec8-98c4-b3bf34e2c572", "d3b07384-d113-4ec8-98c4-b3bf34e2c573"],
  "TenantId": "tenant-a"
}
```

#### Delete Dead Letters Endpoint

- **Path**: `/dead-letters/`
- **Method**: `DELETE`
- **Description**: Permanently removes specified dead letter messages from the system. Use this operation to clear messages that are no longer needed or cannot be successfully reprocessed.
- **Request Body**: `DeadLetterEnvelopeIdsRequest` (same shape as the replay endpoint above).

**Request Example**:

```json
{
  "Ids": ["d3b07384-d113-4ec8-98c4-b3bf34e2c574", "d3b07384-d113-4ec8-98c4-b3bf34e2c575"],
  "TenantId": "tenant-a"
}
```

### Conclusion

By integrating the Dead Letters REST API into your WolverineFX application, you gain fine-grained control over the management of dead letter messages. This feature not only aids in debugging and resolving processing issues but also enhances the overall reliability of your message-driven applications.
