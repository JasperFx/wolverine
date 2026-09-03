# LLM Callouts

::: tip
`WolverineFx.AI` is all about **one shot** calls to an LLM. Multi-turn, tool calling agents are a completely
separate concern that's being designed separately in [GH-4226](https://github.com/JasperFx/wolverine/issues/4226).
:::

## An LLM Call is Just a Message

Let's say you want to ask a large language model to triage an incident as part of processing a message. The
obvious way to do that is to inject an `IChatClient` into your handler and await it right there. That certainly
works, but think about what you've just done: your handler is now holding a database transaction and an unacked
message open for however long the model takes to answer -- and models are slow, rate limited, and occasionally
just down.

You've seen this problem before, and so has Wolverine. Slow, flaky, remote work that you'd like to retry, throttle,
and observe is exactly what messaging is for. So instead of awaiting the model inline, ask for the answer by
returning a message:

<!-- snippet: sample_llm_callout_from_a_handler -->
<a id='snippet-sample_llm_callout_from_a_handler'></a>
```cs
public static class AlertSeenHandler
{
    // The callout is returned as a cascading message next to the storage action, so it is enrolled
    // in the same outbox as the write: a callout cannot fire for a transaction that did not commit.
    public static (IStorageAction<Incident>, LlmCallout) Handle(AlertSeen message, Incident incident)
    {
        return (Storage.Update(incident),
            LlmCallout.Ask<IncidentTriage>(Prompts.Triage, incident.Snapshot()).Tagged("triage"));
    }
}

// The answer arrives as an ordinary message, with an ordinary handler, an ordinary retry policy,
// and its own place in the correlation chain.
public static class TriageHandler
{
    public static void Handle(IncidentTriage triage)
    {
        // page someone, open a ticket, whatever the severity calls for
    }
}
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Extensions/Wolverine.AI.Tests/Samples.cs#L63-L86' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_llm_callout_from_a_handler' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

`LlmCallout` is an ordinary Wolverine message that happens to be handled by calling a model. Being an ordinary
message gets you quite a lot for free:

* The callout is enrolled in the **transactional outbox** right alongside the write next to it, so a callout can
  never fire for a transaction that rolled back, and can't be lost if the process restarts in between
* **Retries and error policies** apply, so a 503 from your provider is retried on a cooldown schedule
* **Back pressure** applies, so the callout queue caps how many calls are in flight regardless of how fast you
  publish them
* **Scheduling** applies, so `DeliveryOptions.ScheduleDelay` delays a callout like anything else
* Every callout is an **envelope**, so correlation and causation ids already tell you where a model call came from
  and what it caused

The model's answer comes back as a *completely ordinary message* with its own handler, its own retry policy, and
its own spot in the correlation chain.

::: info
This is publish only. There's deliberately no correlated request/reply flavor and no waiting on an answer --
it's pub/sub async workflows all the way down.
:::

## Getting Started

Install the package:

```bash
dotnet add package WolverineFx.AI
```

`WolverineFx.AI` only depends on the **Microsoft.Extensions.AI abstractions**, and never on any vendor's SDK. This
is the same bargain as `ILogger`: Wolverine binds to the BCL-blessed abstraction, and which provider you use --
Anthropic, OpenAI, Azure, Ollama, something running on your laptop -- is entirely up to you. Registering the
`IChatClient` is your job, which also means any middleware you want to wrap around it stays your call:

<!-- snippet: sample_llm_callout_bootstrapping -->
<a id='snippet-sample_llm_callout_bootstrapping'></a>
```cs
public static class Bootstrapping
{
    public static async Task<IHost> BuildHost(IChatClient chatClient)
    {
        return await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                // Registering the IChatClient is yours. Wolverine.AI only references the
                // Microsoft.Extensions.AI abstractions, so the provider -- and any middleware
                // over it, like UseOpenTelemetry() or UseDistributedCache() -- is your choice.
                opts.Services.AddSingleton(chatClient);

                opts.AddLlmCallouts(ai =>
                {
                    ai.DefaultModelId = "claude-sonnet-5";
                    ai.DefaultSystemPrompt = "You are an experienced site reliability engineer.";

                    // Back pressure: at most this many calls to the model are in flight at once,
                    // no matter how fast callouts are published.
                    ai.MaximumParallelCallouts = 5;

                    // Spend guardrails, enforced as middleware on the callout queue.
                    ai.Budget.MaximumPromptCharacters = 20_000;
                    ai.Budget.MaximumTokensPerWindow = 200_000;
                    ai.Budget.Window = 1.Minutes();
                });
            }).StartAsync();
    }
}
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Extensions/Wolverine.AI.Tests/Samples.cs#L16-L48' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_llm_callout_bootstrapping' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

`AddLlmCallouts()` puts every callout in your application on one dedicated, durable local queue named
`llm-callouts` (there's a `LlmCallout.QueueName` constant if you need to refer to it). Durability is on by default
and it's rather the whole point, so only turn it off with `ai.DurableQueue = false` if your application has no
message persistence configured at all.

## Asking for a Structured Answer

`LlmCallout.Ask<TResponse>()` asks the model for an answer shaped like `TResponse`. Wolverine builds a JSON schema
from that type, hands it to the model as the response format, reads the answer back, and publishes it as an
ordinary message:

```csharp
LlmCallout.Ask<IncidentTriage>("Classify this incident.", incident.Snapshot());
```

The second argument is optional context. It gets serialized to JSON and appended underneath your prompt at the
moment you create the callout, which means the exact text the model will be asked is baked into the message. That
matters more than it might sound: a callout sitting in your dead letter queue can be read and understood without
re-running anything at all.

::: tip
Prompts are yours. Wolverine isn't trying to be a prompt templating framework here -- a `const string`, a record,
Scriban, whatever you already like, it's all the same to `LlmCallout` because by the time a callout exists the
prompt is just text.
:::

### Asking for Plain Text

`LlmCallout.Ask()` with no type argument asks for plain text, and publishes an `LlmTextResponse` carrying both the
answer and the callout that produced it. Every text callout in your application publishes that same type, so a
handler tells one kind of callout from another by looking at its `Tag`.

If that starts turning into a switch statement pretending to be a type, take it as a hint to move over to the
structured flavor and let each kind of answer be its own message.

## Triggering a Callout from a Projection

Here's a nice payoff from callouts being messages: event store integration needed no new concepts at all.
`RaiseSideEffects()` on the JasperFx.Events projection base class already publishes messages atomically with the
projection update, and a callout is just a message:

<!-- snippet: sample_llm_callout_from_a_projection -->
<a id='snippet-sample_llm_callout_from_a_projection'></a>
```cs
public class IncidentProjection : SingleStreamProjection<Incident, Guid>
{
    public override ValueTask RaiseSideEffects(IDocumentOperations operations, IEventSlice<Incident> slice)
    {
        if (slice.Snapshot is { IsEscalated: true } incident &&
            slice.Events().OfType<IEvent<IncidentEscalated>>().Any())
        {
            slice.PublishMessage(LlmCallout
                .Ask<IncidentTriage>("Classify this incident and recommend a next action.", incident)
                .Tagged("triage")

                // Stream id plus version is the natural logical identity here: a daemon retry that
                // reprocesses this slice republishes the identical callout, and this is what lets
                // deduplication recognize it as the same intent rather than a second one.
                .DeduplicatedBy($"{incident.Id}:{slice.Events().Last().Version}"));
        }

        return new ValueTask();
    }
}
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Samples/DocumentationSamples/LlmCalloutSamples.cs#L27-L50' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_llm_callout_from_a_projection' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

One integration covers Marten, Polecat, and Fisher alike. There are two behaviors worth knowing about here.

First, **rebuilds don't re-trigger callouts**, because side effects are suppressed during a projection rebuild by
default. That's exactly what you want -- rebuilding a projection across two years of history should not re-triage
two years of incidents, and should certainly not bill you for the privilege.

Second, **the async daemon can republish**. If a slice fails partway through it gets reprocessed, and your callout
goes out again. For callouts that can be republished this way, give them a logical identity and turn on
enforcement:

```csharp
opts.Durability.EnableMessageDeduplication();
opts.AddLlmCallouts(ai => ai.DeduplicateCallouts = true);
```

Stream id plus version is usually the natural key, and that's what the sample above uses. The id rides along on
the message either way; `DeduplicateCallouts` is what makes Wolverine actually claim it before calling the model.
Callouts with no id still execute, since realistically only the republish-prone sources have a natural key to give
you.

## Controlling How Callouts Are Processed

Callouts execute on their own local queue named `llm-callouts`, and the reason that matters is that a model
provider is the slowest, flakiest, most expensive thing your application talks to. You almost certainly do not want
an unbounded number of requests going out at once just because something upstream published a burst of them.

`MaximumParallelCallouts` is the knob for that, and everything else a local queue can do is available through
`ConfigureQueue`:

<!-- snippet: sample_llm_callout_queue_configuration -->
<a id='snippet-sample_llm_callout_queue_configuration'></a>
```cs
public static class QueueConfiguration
{
    public static void Configure(WolverineOptions opts)
    {
        opts.AddLlmCallouts(ai =>
        {
            // The back pressure knob. Five callouts may be talking to the model at once; the sixth
            // waits its turn on the queue instead of piling another request onto your provider.
            ai.MaximumParallelCallouts = 3;

            // How long any single callout may run before it is cancelled and retried.
            ai.Timeout = 90.Seconds();

            // Anything else you would do to a local queue, you can still do here.
            ai.ConfigureQueue(queue =>
            {
                // Stop calling the model at all once it starts failing consistently, instead of
                // burning the retry schedule on a provider that is plainly down.
                queue.CircuitBreaker(cb =>
                {
                    cb.MinimumThreshold = 10;
                    cb.FailurePercentageThreshold = 20;
                    cb.PauseTime = 2.Minutes();
                });
            });
        });
    }
}
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Extensions/Wolverine.AI.Tests/Samples.cs#L139-L170' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_llm_callout_queue_configuration' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

::: tip
`MaximumParallelCallouts` caps how many calls are *in flight*, not how many are published. Callouts beyond the
limit sit on the queue and wait their turn, which is exactly what you want -- the queue is durable, so waiting is
free and losing them is not.
:::

Some providers are stricter than that, and a per-account rate limit that only lets you have one conversation going
at a time is not unusual. Take the queue down to strict ordering when that's the situation you're in:

<!-- snippet: sample_llm_callout_sequential_queue -->
<a id='snippet-sample_llm_callout_sequential_queue'></a>
```cs
public static class SequentialCallouts
{
    public static void Configure(WolverineOptions opts)
    {
        // A provider on a tight per-account rate limit, or a model you are only allowed one
        // conversation with at a time: process callouts strictly one at a time, in order.
        opts.AddLlmCallouts(ai => ai.ConfigureQueue(queue => queue.Sequential()));
    }
}
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Extensions/Wolverine.AI.Tests/Samples.cs#L172-L184' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_llm_callout_sequential_queue' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

### The Answer Has Its Own Queue

Here's the part that's easy to miss. The model's answer comes back as an ordinary cascading message, which means
it gets routed and handled like anything else in your system -- and it is *not* on the callout queue. Its
parallelism, its durability, and its ordering are all configured separately, the same way you'd configure them for
any other message:

<!-- snippet: sample_llm_response_queue_configuration -->
<a id='snippet-sample_llm_response_queue_configuration'></a>
```cs
public static class ResponseQueueConfiguration
{
    public static void Configure(WolverineOptions opts)
    {
        opts.AddLlmCallouts(ai => ai.MaximumParallelCallouts = 10);

        // The answer is an ordinary message, so the queue it lands on is configured the ordinary
        // way. Ten callouts can be in flight against the model while the work their answers kick
        // off -- paging someone, writing to a downstream system -- runs one at a time.
        opts.LocalQueueFor<IncidentTriage>()
            .Sequential()
            .UseDurableInbox();
    }
}
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Extensions/Wolverine.AI.Tests/Samples.cs#L186-L203' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_llm_response_queue_configuration' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

This is more useful than it sounds. The two sides of a callout have genuinely different constraints: talking to the
model wants throughput within whatever your provider will tolerate, while the work the answer kicks off might be
writing to a downstream system that wants one thing at a time. Tuning them together would mean picking the worse of
the two numbers.

## Budgets and Failures

The expensive way for this to go wrong is a runaway prompt, or a loop that publishes callouts faster than anyone
notices. Since advice in a documentation page has never once stopped that from happening, the guardrails are
middleware on the callout queue instead:

```csharp
opts.AddLlmCallouts(ai =>
{
    ai.Budget.MaximumPromptCharacters = 20_000;
    ai.Budget.MaximumTokensPerWindow = 200_000;
    ai.Budget.Window = 1.Minutes();
});
```

`MaximumPromptCharacters` refuses a callout *before* your provider is ever called, so a context you accidentally
assembled out of an unbounded collection costs you nothing at all. `MaximumTokensPerWindow` refuses callouts once
this node has burned through its allowance, counted from the token usage your provider actually reported back.

::: warning
The token ledger is **per process**, not cluster wide. If you're running a fleet of N nodes, the real ceiling is N
times whatever you configured. Think of it as a circuit breaker against a runaway loop rather than as billing
enforcement.
:::

Both limits **dead letter instead of retrying**, and so does an answer that can't be parsed into the response type
you asked for. The reasoning is the same in all three cases: a callout that's over budget will be over budget on
every single attempt, and a prompt the model can't answer in the shape you wanted will produce the same unusable
answer every time you ask. Retrying either one is precisely the runaway spend the guardrails exist to prevent. The
raw text the model sent back is carried on `LlmCalloutException.RawResponse` so you can triage a dead letter
without re-running it.

Everything else -- a 503, a socket reset, a timeout -- is treated as transient and gets the cooldown schedule:

```csharp
opts.AddLlmCallouts(ai => ai.RetryCooldowns = [1.Seconds(), 5.Seconds(), 15.Seconds()]);
```

`LlmCalloutOptions.Timeout` caps any single callout at two minutes by default. Do note that this depends on your
registered `IChatClient` honoring its cancellation token, though every HTTP based provider does.

### Bringing Your Own Error Handling

The defaults are opinionated, and eventually you'll want something else. The awkward bit is that the `LlmCallout`
handler ships inside `WolverineFx.AI` rather than in your assemblies, so there's no handler class of yours to hang
a `Configure(HandlerChain)` method on. Use an `IHandlerPolicy` instead, which is how Wolverine.AI applies its own
rules in the first place:

<!-- snippet: sample_llm_callout_error_handling -->
<a id='snippet-sample_llm_callout_error_handling'></a>
```cs
// The LlmCallout handler ships inside WolverineFx.AI, so you cannot drop a Configure(HandlerChain)
// method onto it the way you would with one of your own handlers. An IHandlerPolicy reaches the same
// chain, and it is exactly how Wolverine.AI applies its own defaults.
public class CalloutErrorPolicy : IHandlerPolicy
{
    public void Apply(IReadOnlyList<HandlerChain> chains, GenerationRules rules, IServiceContainer container)
    {
        foreach (var chain in chains.Where(x => x.MessageType == typeof(LlmCallout)))
        {
            // A provider rate limit is worth waiting out rather than hammering.
            chain.OnException<HttpRequestException>()
                .RetryWithCooldown(5.Seconds(), 30.Seconds(), 2.Minutes());

            // A callout that timed out may well have cost you a completion you never saw. Give it one
            // more attempt, then get it out of the queue rather than paying for it a third time.
            chain.OnException<TaskCanceledException>()
                .RetryOnce()
                .Then.MoveToErrorQueue();
        }
    }
}

public static class CalloutErrorHandling
{
    public static void Configure(WolverineOptions opts)
    {
        // Opt out of the built in cooldown schedule so your rules are the whole story. Leave this
        // alone and both sets apply, with Wolverine.AI's defaults added first.
        opts.AddLlmCallouts(ai => ai.RetryCooldowns = []);

        opts.Policies.Add(new CalloutErrorPolicy());
    }
}
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Extensions/Wolverine.AI.Tests/Samples.cs#L205-L241' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_llm_callout_error_handling' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

::: warning
Setting `RetryCooldowns` to an empty array is what opts you out of the built in schedule. If you leave it alone,
both sets of rules apply and Wolverine.AI's are added first, which is usually not what you meant when you sat down
to write your own.
:::

The answer message is the easy case, since that handler is yours. Configure it the ordinary way:

<!-- snippet: sample_llm_response_error_handling -->
<a id='snippet-sample_llm_response_error_handling'></a>
```cs
// The answer is an ordinary message with an ordinary handler, so its error handling is configured the
// ordinary way -- and separately from the callout's. That separation is the point: a failure in your
// own downstream work should not send you back to the provider for a second bill.
public record OutageSummary(string Headline, string NextStep);

public static class OutageSummaryHandler
{
    public static void Configure(HandlerChain chain)
    {
        chain.OnException<TimeoutException>()
            .RetryWithCooldown(1.Seconds(), 5.Seconds());

        chain.OnException<InvalidOperationException>()
            .MoveToErrorQueue();
    }

    public static void Handle(OutageSummary summary)
    {
        // file the incident report, notify the channel, whatever the summary calls for
    }
}
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Extensions/Wolverine.AI.Tests/Samples.cs#L243-L267' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_llm_response_error_handling' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

Keeping those two separate is deliberate. If your own downstream work throws -- the ticketing system is down, say
-- you want to retry *that*, not go back to the provider and pay for the same completion a second time. The model
already answered you. That answer is a durable message now, and it will still be there when the ticketing system
comes back.

## Observability

Callouts show up in Wolverine's own metrics, logging, and Open Telemetry spans just like any other message, keyed
by the `llm-callouts` queue. On top of that, `WolverineFx.AI` publishes token counters on a `Wolverine.AI` meter,
tagged by the callout's `Tag` and by the model that answered:

| Instrument | Description |
| --- | --- |
| `wolverine.ai.callout.input_tokens` | Input tokens consumed |
| `wolverine.ai.callout.output_tokens` | Output tokens produced |
| `wolverine.ai.callout.total_tokens` | Total tokens billed |

::: tip
If you want the full GenAI semantic convention spans, add Microsoft.Extensions.AI's own `.UseOpenTelemetry()`
middleware when you register the `IChatClient`. Wolverine isn't trying to duplicate that. What Wolverine adds is
the labelling that middleware can't see: *which callout* the spend belongs to.
:::

## Testing

Being message shaped makes this easy to test without a model anywhere in sight, and honestly that's most of the
argument for the design. `StubChatClient` ships in the package as a scripted `IChatClient`:

<!-- snippet: sample_llm_callout_testing -->
<a id='snippet-sample_llm_callout_testing'></a>
```cs
[Fact]
public async Task triage_an_escalated_incident()
{
    // A scripted IChatClient: no key, no network, no model.
    var chat = new StubChatClient()
        .Respond(new IncidentTriage("high", "page the on-call"));

    using var host = await Host.CreateDefaultBuilder()
        .UseWolverine(opts =>
        {
            opts.Services.AddSingleton<IChatClient>(chat);
            opts.AddLlmCallouts(ai => ai.DurableQueue = false);
        }).StartAsync(TestContext.Current.CancellationToken);

    var session = await host.InvokeMessageAndWaitAsync(new AlertRaised("INC-1", "database is on fire"));

    // Assert on the callout the handler produced...
    var callout = session.Sent.SingleMessage<LlmCallout>();
    callout.ExpectsResponse<IncidentTriage>().ShouldBeTrue();
    callout.Tag.ShouldBe("triage");

    // ...on what was actually sent to the model...
    chat.Requests.ShouldHaveSingleItem().Prompt.ShouldContain("INC-1");

    // ...and on the answer coming back as an ordinary message.
    session.Received.SingleMessage<IncidentTriage>().Severity.ShouldBe("high");
}
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Extensions/Wolverine.AI.Tests/Samples.cs#L90-L120' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_llm_callout_testing' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

Answers come back in the order you queued them, and running out of script is an error rather than a repeat. That's
on purpose: a test that quietly hands the last answer back for a callout it didn't know it was making is a test
that passes for the wrong reason. There's also `Throw()` to script a failure, and `RespondAfter()` to script a slow
answer if you want to exercise the timeout.

Better yet, a handler that returns a callout is a pure function, so the test that's actually worth writing needs no
host at all:

<!-- snippet: sample_llm_callout_unit_test -->
<a id='snippet-sample_llm_callout_unit_test'></a>
```cs
[Fact]
public void the_handler_asks_for_a_triage()
{
    var incident = new Incident("INC-1", "database is on fire", true);

    var (_, callout) = AlertSeenHandler.Handle(new AlertSeen("INC-1"), incident);

    callout.ExpectsResponse<IncidentTriage>().ShouldBeTrue();
    callout.Prompt.ShouldBe(Prompts.Triage);
    callout.Context.ShouldNotBeNull().ShouldContain("INC-1");
}
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Extensions/Wolverine.AI.Tests/Samples.cs#L122-L136' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_llm_callout_unit_test' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

## Trimming and AOT

`WolverineFx.AI` is trim and AOT compatible, but you have to tell it about your response types twice -- once so it
can resolve them, and once for JSON:

```csharp
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(IncidentTriage))]
[JsonSerializable(typeof(IncidentSnapshot))]
internal partial class AiJsonContext : JsonSerializerContext;

opts.AddLlmCallouts(ai =>
{
    ai.JsonSerializerOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web)
    {
        TypeInfoResolver = AiJsonContext.Default
    };

    ai.RegisterResponseType<IncidentTriage>();
});
```

`RegisterResponseType<T>()` is what turns the identifier a persisted callout carries back into a `Type` without
needing `Type.GetType()`. The `JsonSerializerContext` covers the other two reflective spots, and it covers both of
them at once -- Microsoft.Extensions.AI builds the response schema from whatever type information your serializer
options resolve, so a source generated context gets you a source generated schema with no extra machinery.

Neither of these is required outside a trimmed application, where the message type registry and `Type.GetType()`
cover the same ground perfectly well. Registering them anyway costs you a line each and turns a possible failure
into a loud one at startup rather than a quiet one in your dead letter queue.

There's one authoring API with no trim clean form, because serializing an arbitrary object means reflecting over
its shape:

```csharp
// warns IL2026 / IL3050 under trim analysis
LlmCallout.Ask<IncidentTriage>(prompt, incident.Snapshot());

// the trim clean equivalent
LlmCallout.Ask<IncidentTriage>(prompt)
    .WithContext(incident.Snapshot(), AiJsonContext.Default.IncidentSnapshot);
```

There's also a `WithContext(string)` that just takes JSON you produced yourself, which is handy anyway when your
context was never a CLR object to begin with -- a rendered document, a passage you retrieved, a diff.

::: warning
A `JsonSerializerContext` that's missing a response type does **not** throw. Schema generation quietly answers with
an *empty* schema, which means the model gets handed a constraint that constrains nothing. It will usually still
answer you, and the answer will usually still parse, so this shows up looking like a model quality problem rather
than the configuration problem it actually is. If your structured answers get vague right after a trimmed publish,
check this first.
:::

## Why `LlmCallout` Isn't Generic

`LlmCallout.Ask<TResponse>()` is generic, but `LlmCallout` itself is not -- the response type rides along on the
message as data. That was a deliberate reversal of the original design, and the reasoning is recorded in
[GH-4227](https://github.com/JasperFx/wolverine/issues/4227).

The short version is that a callout has to survive being written to a durable inbox and read back after a restart.
Wolverine's message type registry is a flat name lookup, so a closed `LlmCallout<IncidentTriage>` coming back on a
cold start has no way back to a `Type` unless every response type in your application was enumerated at bootstrap
-- and the recovery sweep runs before your application has published a single callout of its own. On top of that,
the type argument carries no behavior whatsoever. Its entire job is to name a type twice, once for the schema and
once for the deserialization.

One message type buys you one handler chain, one dead letter identity, one queue, and one place for the budget
middleware to live. The typing that actually matters to you -- the handler that receives the answer -- is exactly
the same either way.

## What's Not Here

* **Agents, tools, and model loops.** That's tier 2, and it's tracked in [GH-4226](https://github.com/JasperFx/wolverine/issues/4226).
* **Embedding generation.** `IEmbeddingGenerator<,>` is the obvious seam for event store integrations, but that's
  separate adapter work.
