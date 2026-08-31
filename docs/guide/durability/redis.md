# Redis Persistence <Badge type="tip" text="6.32" />

::: danger Read this before you use it for sagas
**Redis is usually deployed as a cache, and a cache is allowed to lose your data.**

Two defaults do it, independently:

- **Eviction.** A server running `maxmemory-policy allkeys-lru` (or any other `allkeys-*` policy) will
  delete a live saga to make room for something else. There is no error, no log line, no exception —
  the key is simply gone, and the next message for that saga either starts a second one or fails with
  `UnknownSagaException`. Nothing in any client can tell that apart from a saga that never existed.
- **Durability.** Redis persists asynchronously. With RDB snapshots, a crash loses everything written
  since the last snapshot. With `appendfsync everysec` — the AOF default — a crash loses up to a
  second of writes. With neither configured, a restart loses everything.

Saga state in Redis is only safe if the operator has **configured persistence** and **excluded these
keys from eviction**. That is an operational commitment, not a code change, and it is made by whoever
runs the Redis rather than by whoever writes the handler. Wolverine
[probes for the worst of it at startup](#the-startup-check), but the probe is not a guarantee — see
[when to use this, and when not to](#when-to-use-this-and-when-not-to).

If you would not accept losing a saga, keep sagas in Marten, Polecat, EF Core, RavenDb, CosmosDB or a
relational database. Those are what the rest of the Critter Stack is built around; this is for the
narrower case described below.
:::

::: tip
This page is about `WolverineFx.Redis` as a **place to keep entities**. The same package is also the
[Redis Streams transport](/guide/messaging/transports/redis), which is a different feature solving a
different problem. They share a package and a `StackExchange.Redis` dependency and nothing else — use
either, or both.

This does **not** make Redis the message store. The transactional inbox and outbox stay with whichever
database your application already uses.
:::

Some state genuinely belongs in Redis. A rate-limit tally, a short-lived reservation, a shipping quote
that stops being interesting after half an hour, a projection cached in front of a slower store. Those
things are already in Redis in most systems; what they are missing is a way for a Wolverine handler to
reach them without dropping out of [declarative persistence](/guide/handlers/persistence) entirely —
injecting a multiplexer, `await`-ing it, and re-implementing the "what if it is not there" half by
hand in every handler that reads it.

This package teaches Wolverine to read and write registered types as Redis keys, so a plain `[Entity]`
parameter and the declarative `Storage.Store()` / `Storage.Delete()` return values work against Redis.

```sh
dotnet add package WolverineFx.Redis
```

## Registering documents

```csharp
builder.Services.AddSingleton<IConnectionMultiplexer>(
    ConnectionMultiplexer.Connect("localhost:6379"));

builder.Host.UseWolverine(opts =>
{
    opts.UseRedisPersistence(redis =>
    {
        redis.Store<ShippingQuote>(x =>
        {
            x.KeyFor = ctx => $"quote:{ctx.TenantId}:{ctx.Id}";
            x.ExpiresAfter = TimeSpan.FromMinutes(30);
        });
    });
});
```

`IConnectionMultiplexer` comes from your own registration, so the connection keeps whatever
authentication, reconnect policy and token refresh the rest of your application uses. It is
deliberately **not** taken from the Redis transport: the transport owns its multiplexer's lifetime, is
often pointed at a different Redis than the application's data, and does not have to be configured at
all for this to be used.

Registration is explicit, type by type, and `KeyFor` is **required**. There is deliberately no default
key layout — the identity-to-key mapping is the part only the application knows, and a key Wolverine
invented would collide with whatever else already lives in that keyspace.

Explicit registration is also what keeps this provider **selective**. Wolverine resolves an entity to
the first persistence provider that claims its type, so a provider that claimed anything Redis could
theoretically hold would compete with Marten or EF Core for their own documents. This one claims only
what you registered, and is otherwise invisible.

### The key function

`KeyFor` receives a `RedisKeyContext` — the entity type, the resolved identity, and the tenant — and
returns the Redis key:

```csharp
x.KeyFor = ctx => $"quote:v{Generation}:{ctx.TenantId}:{ctx.Id}";
```

`TenantId` is null when there is no tenant; Wolverine's default-tenant sentinel is normalised away, so
a key function never has to know it exists.

### Expiry

Redis expires keys natively, so unlike an object store this can honestly be offered:

```csharp
x.ExpiresAfter = TimeSpan.FromMinutes(30);
```

Wolverine re-applies the TTL on every write, so the window slides forward from the *last* write rather
than from the first. Leave it null and the key never expires; Wolverine never removes a TTL it did not
set.

On a saga this is a destructor with no ceremony. An expired saga is simply gone — the next message for
that identity either starts a new one or fails with `UnknownSagaException`. It is not a substitute for
a [timeout message](/guide/durability/sagas#timeout-messages), which lets the saga run code before it
disappears.

### Other options

| Option | Meaning |
|---|---|
| `Database` | The numbered Redis database. `-1`, the default, means the multiplexer's own. Ignored by Redis Cluster, which only has database 0. |
| `Serializer` | `System.Text.Json` by default. Implement `IRedisDocumentSerializer` for a different format, an envelope around the payload, or encryption. |
| `IdentityType` | The CLR type of the identity, if it is not the type of the entity's own identity member. |

## Reading and writing

A registered type resolves through `[Entity]` like any other, keeping `Required`, `OnMissing` and
`MissingMessage`:

```csharp
[WolverineGet("/api/quotes/{id}")]
public static ShippingQuote Get(
    [Entity(OnMissing = OnMissing.ProblemDetailsWith404,
        MissingMessage = "That quote has expired")]
    ShippingQuote quote) => quote;
```

The declarative storage return values work unchanged:

```csharp
public static IStorageAction<ShippingQuote> Handle(RequestQuote command)
{
    return Storage.Store(new ShippingQuote(command.Id, command.Amount));
}
```

For documents, `Insert`, `Update` and `Store` are all the same write — a `SET` overwrites whatever is
at the key, so these are last-write-wins. You can also take `IRedisDocumentSession` directly for the
same key mapping outside a handler.

## Sagas <Badge type="tip" text="6.32" />

Sagas are a **separate registration** from documents, and each refuses the other's type:

```csharp
opts.UseRedisPersistence(redis =>
{
    redis.Saga<OrderSaga>(x => x.KeyFor = ctx => $"saga:order:{ctx.Id}");
});
```

That separation is load-bearing rather than cosmetic. It is what lets Wolverine claim saga chains and
*only* saga chains: `[Transactional]` and `AutoApplyTransactions` ask the same question — "which
provider owns this chain's transaction?" — and Redis has no transaction Wolverine could own. An
ordinary chain that resolved here would have its transaction taken away from the store that actually
has one. Registering a saga is the explicit statement that makes the exception safe.

### Optimistic concurrency

Every saga write is a compare-and-swap. A saga is one Redis hash carrying a revision counter beside
its state; the read that loads the saga brings that revision with it, and the write refuses unless the
stored revision still matches:

- A **create** that finds a saga already at the key loses, rather than overwriting whatever the winner
  had already put there.
- An **update** whose revision has moved on loses.
- A **completion**, which deletes the key, is a compare-and-swap too — a blind delete would drop a
  concurrent write just as silently as a blind overwrite would.
- An update against a saga that another message has already **completed** is refused rather than
  quietly recreating a saga that is meant to be over.

Every one of those is reported as `SagaConcurrencyException`, which derives from
`JasperFx.ConcurrencyException` exactly as Marten's, EF Core's and CosmosDB's do — so a single policy
covers every store:

```csharp
opts.Policies.OnException<ConcurrencyException>().RetryTimes(3);
```

The mechanism is a **Lua script**, not `WATCH`/`MULTI`/`EXEC`. StackExchange.Redis multiplexes every
caller onto shared connections and `WATCH` is per-connection state, so its transaction support has to
reserve a connection out of the pool to hold that state — while Redis runs a script to completion
before it will run anything else, which is exactly the atomic read-compare-write this needs. One round
trip, no reserved connection, and nothing left dangling by a handler that throws between the read and
the write. Each script touches a single key, so this is also Redis Cluster safe.

### The startup check

Wolverine asks the server, once at startup, whether it is configured in a way that would destroy what
it is being asked to keep:

```csharp
opts.UseRedisPersistence(redis =>
{
    // Warn (default), Throw, or Disabled
    redis.DurabilityCheck = RedisDurabilityCheck.Throw;

    redis.Saga<OrderSaga>(x => x.KeyFor = ctx => $"saga:order:{ctx.Id}");
});
```

It reports an `allkeys-*` eviction policy, and a server with neither AOF nor RDB save points
configured. It warns by default rather than failing, because the question cannot always be asked:
managed Redis offerings routinely block `CONFIG` outright, and a probe that could not read the setting
must not be the reason a deployment fails. Set it to `Throw` where losing a saga is worse than failing
to deploy.

**A quiet startup is not a guarantee.** The check reports the two configurations that are
unambiguously wrong. It cannot tell you whether `appendfsync` is tuned so loosely that a crash loses a
second of sagas, whether the replica you fail over to is caught up, or whether the key was evicted an
hour after the check ran.

## Alongside another store

Redis entities and Marten (or Polecat, Fisher, EF Core, RavenDb, CosmosDB) documents coexist in one
application, and one handler can take an `[Entity]` of each:

```csharp
public static IMartenOp Handle(
    ApproveOrder command,
    [Entity] Order order,          // Marten
    [Entity] ShippingQuote quote)  // Redis
{
    // ...
}
```

Nothing has to be said to make that work. Wolverine consults **selective** providers ahead of catch-all
document stores, so a type you registered resolves to Redis and everything else falls through to
whichever store would have had it anyway — whichever order the two integrations were registered in.

Two consequences worth knowing:

- **A saga belongs to exactly one of them.** A saga you registered with `Saga<T>()` is kept in Redis;
  every other saga is kept by Marten or your database exactly as before.
- **There is no atomicity across the two.** The Marten write commits in its transaction; the Redis
  write already happened. A handler that writes both and then throws has written the Redis key and not
  the Marten document. If that matters, write the Redis key from a projection or a follow-on message
  triggered by the committed Marten write, rather than in the same handler.

## What this deliberately does not do

**No message store.** Redis is a poor transactional inbox and outbox, and this package does not offer
to be one. Durability stays with Postgres, SQL Server, Marten or whichever database you already use.

**No unit of work.** Redis has no transaction Wolverine could enlist a handler in, so every write takes
effect immediately. A handler that writes two entities and then throws has written one of them. Each
saga write is individually atomic — it is one Lua script — but two of them are still two writes.

**No querying.** `[All]`, `[FirstOrDefault]` and `[Queryable]` are not supported and fail at
bootstrapping naming this provider. `SCAN` over a key pattern is a cursor walk of the whole keyspace,
not a query, and a scan that looks like a query is worse than an honest refusal.

**No soft delete.** `MaybeSoftDeleted` does not apply. Only your serializer knows what deleted means
for its payload; answer `null` for anything that should count as missing.

**No compare-and-swap on storage actions.** `Storage.Store()` and friends are last-write-wins even for
a saga type. The handler produced that entity out of thin air rather than out of a read Wolverine
tracked, so there is no revision to compare against. Compare-and-swap belongs to the saga chain, which
does have one.

## When to use this, and when not to

Reach for Redis persistence when the state is **already Redis-shaped**: short-lived, cheap to
reconstruct, or explicitly disposable. A rate-limit tally, a cached read model, a quote that expires
in half an hour, a reservation that is meant to lapse. The `ExpiresAfter` option exists because that is
the workload this fits.

For sagas, be more careful. The concurrency guarantee here is real and tested — a stale write loses,
every time. What Redis cannot give you is the *durability* guarantee that sits underneath it. A
compare-and-swap against a revision that was evicted, or lost in the second before a crash, is a
correct answer to the wrong question.

So: Redis sagas are a reasonable fit for a saga that is **short-lived and reconstructible** — one whose
loss is an annoyance rather than a correctness failure, in a Redis that the same team operates and has
deliberately configured as a store. They are a poor fit for a long-running business process, for
anything financial, for anything where "we lost some sagas last Tuesday" is not an acceptable
sentence, and for any Redis you share with a caching workload — because the eviction policy that
workload wants is the one that deletes your sagas.

If you are not sure which of those you have, use a database. The saga will be faster to reason about
than to recover.
