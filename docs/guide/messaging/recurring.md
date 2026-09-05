# Recurring Messages <Badge type="tip" text="6.34" />

Wolverine has long supported [scheduled message delivery](/guide/messaging/message-bus#scheduling-message-delivery-or-execution)
for one-off, "run this later" messages. `opts.Schedules` builds recurring, cron-driven messages on
top of exactly that machinery: a single agent per cluster keeps the *next* occurrence of every
registered schedule pre-scheduled through the ordinary scheduled-message pipeline, and everything
else — delivery, durability, replay after a crash, management tooling — is the infrastructure that
already ships.

<!-- snippet: sample_registering_recurring_messages -->
<a id='snippet-sample_registering_recurring_messages'></a>
```cs
var builder = Host.CreateApplicationBuilder();
builder.UseWolverine(opts =>
{
    // Simplest possible usage: a message type with a public, no-argument
    // constructor, published on a cron schedule. The schedule's name defaults
    // to the message type's name
    opts.Schedules.RecurringMessage<RunNightlyRollup>("0 2 * * *");

    // Or build the message per occurrence — the factory is handed the
    // occurrence time, so a message can describe the window it covers
    opts.Schedules.RecurringMessage(
        "daily-report",
        "0 9 * * *",
        occurrence => new BuildDailyReport(occurrence.AddDays(-1), occurrence));

    // Cron expressions parse into a first class value type that you can
    // construct, hold, and reuse — including with an explicit time zone
    var nineAmCentral = new CronSchedule(
        "0 9 * * *",
        TimeZoneInfo.FindSystemTimeZoneById("America/Chicago"));

    opts.Schedules.RecurringMessage<SendMorningDigest>(nineAmCentral);
});
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Samples/DocumentationSamples/RecurringMessageSamples.cs#L37-L63' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_registering_recurring_messages' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

The message types are handled like any other message — the cron machinery only decides *when* an
occurrence is published, never how it is processed:

<!-- snippet: sample_recurring_message_types -->
<a id='snippet-sample_recurring_message_types'></a>
```cs
// Recurring messages are handled like any other message — the cron machinery
// only decides WHEN they are published, never how they are processed
public record RunNightlyRollup;

public record BuildDailyReport(DateTimeOffset From, DateTimeOffset To);

public record SendMorningDigest;

public static class RecurringSampleHandler
{
    public static void Handle(RunNightlyRollup message)
        => Console.WriteLine("Rolling up!");

    public static void Handle(BuildDailyReport message)
        => Console.WriteLine($"Reporting on {message.From} to {message.To}");

    public static void Handle(SendMorningDigest message)
        => Console.WriteLine("Good morning!");
}
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Samples/DocumentationSamples/RecurringMessageSamples.cs#L67-L89' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_recurring_message_types' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

Registering the first schedule is the feature's opt-in. A host with zero schedules registered
behaves — and migrates its message store — exactly as it did before this feature existed. The first
registration wires three things:

1. **The recurring-message agent**, a [singular agent](/tutorials/leader-election) that runs on
   exactly one node in the cluster and moves on failover. Its whole job is a guarantee: the next
   occurrence of each schedule is published with a `ScheduledTime`, so the envelope sits in the
   same place every other scheduled message sits (the durable inbox when a message store is
   configured) and fires through the same poller.
2. **Occurrence deduplication.** Every occurrence carries a deterministic
   [logical deduplication id](/guide/durability/idempotency) of the form
   `"{scheduleName}:{occurrenceUtc}"` — identical on any node and after any restart — and the
   deduplication middleware is applied to the handler chains of exactly the cron-scheduled message
   types. An agent failover or restart that re-publishes the same occurrence is collapsed at
   consumption rather than executed twice. (The requirement is deliberately non-strict: publishing
   the same message type by hand, without an id, passes through untouched.)
3. **Trace attribution.** Each occurrence carries its schedule's name in a `recurring-schedule`
   envelope header, surfaced on the handler's OpenTelemetry activity as the
   `wolverine.schedule.name` tag, so trace consumers can attribute work to the cron job that
   caused it.

## Cron expressions

`CronSchedule` accepts the standard 5-field cron grammar, or 6 fields where the leading field is
seconds, parsed by [Cronos](https://github.com/HangfireIO/Cronos). Occurrences are computed in the
schedule's time zone (UTC unless one is supplied), with daylight-saving transitions handled
correctly: a schedule inside a spring-forward gap fires at the adjusted instant, and a fall-back
repeat fires once.

An invalid expression throws at the registration call site — a bad schedule is a programming
error, and it should fail at the line that wrote it. So does a cadence faster than every 5
seconds: durable scheduled messages replay on `DurabilitySettings.ScheduledJobPollingTime`
(5 seconds by default), so a faster schedule cannot be honoured and is refused as unsatisfiable
rather than accepted and delivered late.

## Durable tracking: record and verify

When the message store supports it (every relational database provider — PostgreSQL, SQL Server,
MySQL, SQLite), the recurring feature keeps a small tracking table
(`wolverine_recurring_messages`) beside the inbox: one row per registered schedule, mapping the
schedule's name to the envelope id(s) of its pre-scheduled next occurrence. The scheduled inbox
row is still the materialized occurrence — the tracking table is bookkeeping, never a delivery
path — but it upgrades the agent's guarantee from fire-and-forget to **record and verify**:

* Every publish records the pre-scheduled envelope ids on the schedule's row.
* The agent's loop periodically confirms those envelopes still sit `Scheduled` in the inbox, and
  re-publishes the occurrence (same deterministic deduplication id) when something cancelled or
  lost it out from under the schedule.
* After a failover or restart, the successor agent *adopts* its predecessor's verified pending
  occurrence off the row instead of blindly re-publishing it.
* Management tooling can see which schedule owns which pending envelope — and cancel or
  reschedule an occurrence through the ordinary scheduled-message surface — without parsing
  deduplication-id strings.

The table is provisioned only on the main message store and only when at least one schedule is
registered, so the opt-in stays schema-neutral: a host with zero schedules migrates exactly as it
did before the feature existed.

## Pausing and resuming a schedule

Pause/resume is the one piece of runtime-mutable state the feature has — schedule *definitions*
stay code-first. `IRecurringScheduleControl` is registered in the container alongside the first
schedule:

<!-- snippet: sample_pausing_and_resuming_recurring_messages -->
<a id='snippet-sample_pausing_and_resuming_recurring_messages'></a>
```cs
// Registered alongside the first schedule — resolve it straight
// from the container
var control = host.Services.GetRequiredService<IRecurringScheduleControl>();

// Pause: marks the schedule's durable tracking row AND eagerly cancels
// the pre-scheduled next occurrence, so nothing fires in the gap before
// the scheduling agent's next pass — even when this code runs on a
// different node than the agent
await control.PauseAsync("daily-report");

// Resume: the next occurrence is computed strictly after "now".
// The paused window is never back-filled
await control.ResumeAsync("daily-report");

// The tracking rows themselves: which schedule owns which pending
// envelope, next fire times, pause state
var schedules = await control.QueryAsync();
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Samples/DocumentationSamples/RecurringMessageSamples.cs#L12-L32' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_pausing_and_resuming_recurring_messages' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

The semantics, precisely:

* **Pause marks the durable tracking row and eagerly cancels the pending pre-scheduled
  envelope.** The caller may be on a different node than the scheduling agent, so nothing is
  allowed to fire in the gap before the agent's next pass — the mark and the cancel happen in one
  database transaction. Occurrences already *executing* are unaffected.
* **Resume schedules strictly after now.** The first occurrence after a resume is the next one
  the cron computes from the resume instant; the paused window is never back-filled.
* **Pause survives restarts and failovers** — it lives on the row, not in agent memory.
* Pausing or resuming a name that was never registered throws; pausing an already-paused schedule
  is a no-op that keeps the original pause timestamp.

On a message store *without* the tracking extension (including no store at all), pause and resume
degrade to the local agent's memory: they only take effect when the running agent is in the same
process, are lost on restart, and cannot cancel an occurrence that is already pre-scheduled —
that one will still fire. This is a documented degradation of the same kind as the
[no-message-store mode](#running-without-a-message-store) below.

## Failure semantics

All of these are deliberate, and worth knowing before relying on the feature:

* **Missed occurrences are skipped, never back-filled.** The one pre-scheduled envelope still
  fires even with the agent down — it is an ordinary scheduled message — and only occurrences
  *after* it in an agent-less window are lost. This is the reason for the agent shape over a
  self-perpetuating message that schedules its own successor, which dies permanently the first
  time its envelope is discarded. A missed firing is a missed firing, not a dead schedule.
* **Failover and restart are harmless.** The agent's working state is in-memory; re-publishing
  the same occurrence produces the same deduplication id.
* **A failed publish logs and retries on the next tick.** It never kills the scheduling loop.
* **An occurrence with no subscribers or handlers is not a publish.** The agent logs a warning
  (once per schedule) and keeps retrying each pass, so a subscription that appears later — or a
  routing table that was still warming up — still gets the occurrence on time rather than
  silently skipping it.

## Running without a message store

A host with no message store still runs recurring messages — the agent starts directly (there is
no cluster to coordinate) and occurrences ride the in-process scheduling model. Two things degrade,
and Wolverine warns about both at startup:

* An occurrence inside a restart window is lost. The *schedule* itself survives — the agent
  re-establishes the next occurrence from the registrations at startup — but anything that was
  pending in memory when the process died is gone.
* There is no store-backed occurrence deduplication, so an agent restart can double-publish an
  occurrence where a broker's native deduplication does not cover it.

`DurabilityMode.Serverless` and `DurabilityMode.MediatorOnly` are different: those modes run no
agents at all, so a registered schedule would be accepted and then silently never fire. Wolverine
**refuses to start** in those modes with schedules registered, naming the schedules in the
exception — a schedule that quietly never happens is exactly the failure this feature exists to
avoid.
