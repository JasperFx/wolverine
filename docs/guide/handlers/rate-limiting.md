# Rate Limiting

Wolverine can enforce distributed rate limits for message handlers by re-queuing and pausing the listener when limits are exceeded. This is intended for external API usage limits that must be respected across multiple worker nodes.

## Message Type Rate Limits

Use `RateLimit` on a message type policy to set a default limit and optional time-of-day overrides:

```cs
using Wolverine;
using Wolverine.RateLimiting;

opts.Policies.ForMessagesOfType<SendToExternalApi>()
    .RateLimit(RateLimit.PerMinute(900), schedule =>
    {
        schedule.TimeZone = TimeZoneInfo.Utc;
        schedule.AddWindow(new TimeOnly(8, 0), new TimeOnly(17, 0), RateLimit.PerMinute(400));
    });
```

The middleware enforces the limit before handler execution. If the limit is exceeded, Wolverine re-schedules the message and pauses the listener for the computed delay.

## Endpoint Rate Limits

You can also rate limit an entire listening endpoint:

```cs
using Wolverine;
using Wolverine.RateLimiting;

opts.RateLimitEndpoint(new Uri("rabbitmq://queue/critical"), RateLimit.PerMinute(400));
```

Endpoint limits take precedence over message type limits when both are configured.

## Distributed Store

Rate limiting relies on a shared store. By default, Wolverine registers an in-memory store for tests and local development. For production, register a shared store implementation.

### SQL Server

```cs
using Wolverine;
using Wolverine.SqlServer;

opts.PersistMessagesWithSqlServer(connectionString)
    .UseSqlServerRateLimiting();
```

This uses the Wolverine message storage schema by default (same schema as the inbox/outbox tables).

## Scheduling Requirements

Rate limiting re-schedules messages through Wolverine's scheduling pipeline. For external listeners, Wolverine requires durable inboxes to ensure rescheduled messages are persisted correctly.

```cs
opts.ListenToRabbitQueue("critical").UseDurableInbox();
// or: opts.Policies.UseDurableInboxOnAllListeners();
```
