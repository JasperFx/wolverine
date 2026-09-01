# LLM Callouts

::: tip
`WolverineFx.AI` is a new package covering **one shot** calls to a large language model. Multi-turn, tool-calling
agents are a separate, later tier ([GH-4226](https://github.com/JasperFx/wolverine/issues/4226)).
:::

## An LLM Call is a Message

A handler that awaits a model inline holds a database transaction and an envelope open for seconds against a
remote service that is slow, rate limited, and occasionally down. Every one of those problems already has an
answer in Wolverine — it just needs the model call to be a message rather than an inline `await`.

That is all this package is. `LlmCallout` is an ordinary Wolverine message that happens to be handled by calling
a model:

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
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Extensions/Wolverine.AI.Tests/Samples.cs#L58-L81' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_llm_callout_from_a_handler' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

Because the callout is a message, it inherits the whole runtime with no extra work:

* **The outbox.** Returned next to a storage action, the callout is enrolled in the same transaction as the write.
  A callout cannot fire for a transaction that rolled back, and cannot be lost to a restart in between.
* **Retries and error policies.** A 503 from the provider is retried on a cooldown schedule. A prompt the model
  cannot answer is dead lettered.
* **Back pressure.** The callout queue caps how many calls are in flight, no matter how fast callouts are published.
* **Scheduling.** `DeliveryOptions.ScheduleDelay` delays a callout like any other message.
* **Observability.** Every callout is an envelope, so correlation and causation ids already answer "what did this
  model call come from, and what did it cause".

The answer comes back as an *ordinary message* with its own handler, its own retry policy, and its own place in the
correlation chain. There is no correlated request/reply here and no waiting: this is publish only, pub/sub async
workflows all the way down.

## Getting Started

Install the package:

```bash
dotnet add package WolverineFx.AI
```

`WolverineFx.AI` depends only on the **Microsoft.Extensions.AI abstractions**, never on a vendor SDK — the same
bargain as `ILogger`. Registering the `IChatClient` is yours, which means the provider (Anthropic, OpenAI, Azure,
Ollama, a local model) and any middleware over it are your choice:

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
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Extensions/Wolverine.AI.Tests/Samples.cs#L11-L43' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_llm_callout_bootstrapping' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

`AddLlmCallouts()` puts every callout on one dedicated, durable local queue named `llm-callouts`
(`LlmCallout.QueueName`). Durable is the default and the point — turn it off with `ai.DurableQueue = false` only
where the application has no message persistence configured at all.

## Structured Answers

`LlmCallout.Ask<TResponse>(...)` asks for a structured answer. Wolverine derives a JSON schema from `TResponse`,
hands it to the model as the response format, deserializes the answer, and publishes it as an ordinary message:

```csharp
LlmCallout.Ask<IncidentTriage>("Classify this incident.", incident.Snapshot());
```

The second argument is optional context. It is serialized to JSON and appended underneath the prompt at the moment
the callout is created, so what the model will be asked is fully baked into the message. A callout sitting in the
dead letter queue can be read and understood without re-running anything.

Prompts are yours. This package is not a prompt template framework — a `const string`, a record, or whatever
templating you already like all work, because by the time a callout exists the prompt is just text.

### The text flavour

`LlmCallout.Ask(...)` with no type parameter asks for plain text, and publishes an `LlmTextResponse` carrying both
the answer and the callout that produced it. Every text callout in an application publishes that same type, so a
handler tells one kind from another by its `Tag`. When that starts to feel like a switch statement pretending to be
a type, that is the signal to move to the structured flavour and let each answer be its own message.

## From a Projection

Because the vocabulary is messages, event store integration needs nothing new. `RaiseSideEffects` on the
JasperFx.Events projection base already publishes messages atomically with the projection update, and a callout is
just a message:

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

One integration serves Marten, Polecat, and Fisher alike. Two things are worth knowing here:

**Rebuilds do not re-trigger callouts.** Side effects are suppressed during a projection rebuild by default, which
is exactly right — rebuilding a projection over two years of history must not re-triage two years of incidents, and
must not bill you for doing it.

**Daemon retries can republish.** A slice that fails partway through is reprocessed, and the callout is published
again. Give republish-prone callouts a logical identity with `DeduplicatedBy(...)` — stream id plus version is the
natural key — and turn on enforcement:

```csharp
opts.Durability.EnableMessageDeduplication();
opts.AddLlmCallouts(ai => ai.DeduplicateCallouts = true);
```

The id is carried on the message either way; `DeduplicateCallouts` is what makes Wolverine claim it before calling
the model. Callouts without an id still execute, since only the republish-prone sources have a natural key.

## Budgets and Failure

A runaway prompt or a loop that publishes callouts faster than anyone notices is the expensive failure mode here,
so the guardrails are middleware on the callout queue rather than advice in a doc page:

```csharp
opts.AddLlmCallouts(ai =>
{
    ai.Budget.MaximumPromptCharacters = 20_000;
    ai.Budget.MaximumTokensPerWindow = 200_000;
    ai.Budget.Window = 1.Minutes();
});
```

`MaximumPromptCharacters` refuses a callout **before** the provider is called, so a context accidentally assembled
from an unbounded collection costs nothing. `MaximumTokensPerWindow` refuses callouts once the node has burned
through its allowance, counted from the usage the provider actually reported.

::: warning
The token ledger is **per process**, not cluster wide. On a fleet of N nodes the effective ceiling is N times the
configured number. Treat it as a circuit breaker against a runaway loop, not as billing enforcement.
:::

Both limits **dead letter rather than retry**, and so does an answer that cannot be parsed into the requested
response type. A callout that is over budget is over budget on every attempt, and a prompt the model cannot answer
in the requested shape produces the identical unusable answer every time — retrying either one is precisely the
runaway spend the guardrails exist to stop. The raw model output is carried on `LlmCalloutException.RawResponse` so
a dead letter can be triaged without a re-run.

Everything else — a 503, a socket reset, a timeout — is transient and gets the cooldown schedule:

```csharp
opts.AddLlmCallouts(ai => ai.RetryCooldowns = [1.Seconds(), 5.Seconds(), 15.Seconds()]);
```

`LlmCalloutOptions.Timeout` (2 minutes by default) caps any single callout. Note that this depends on the
registered `IChatClient` honouring its cancellation token — every HTTP-based provider does.

## Observability

Callouts appear in Wolverine's own metrics, logs, and OpenTelemetry spans like any other message, keyed by the
`llm-callouts` queue. On top of that, `WolverineFx.AI` emits token counters on a `Wolverine.AI` meter, tagged by
the callout's `Tag` and the responding model:

| Instrument | Description |
| --- | --- |
| `wolverine.ai.callout.input_tokens` | Input tokens consumed |
| `wolverine.ai.callout.output_tokens` | Output tokens produced |
| `wolverine.ai.callout.total_tokens` | Total tokens billed |

For full GenAI semantic convention spans, add Microsoft.Extensions.AI's own
`.UseOpenTelemetry()` middleware when registering the `IChatClient`. What Wolverine adds is the labelling that
middleware cannot see: which *callout* the spend belongs to.

## Testing

The message-shaped design makes testing without a model trivial, which is deliberate — it is most of the argument
for the design. `StubChatClient` is a scripted `IChatClient` that ships in the package:

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
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Extensions/Wolverine.AI.Tests/Samples.cs#L85-L115' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_llm_callout_testing' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

Answers are handed out in the order they were queued, and an exhausted script is an error rather than a repeat: a
test that quietly reuses the last answer for a callout it did not know it was making is a test that passes for the
wrong reason. `Throw(...)` scripts a failure, and `RespondAfter(...)` scripts a slow answer for exercising the
timeout.

Better still, a handler that returns a callout is a pure function, so the most valuable test needs no host at all:

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
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Extensions/Wolverine.AI.Tests/Samples.cs#L117-L131' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_llm_callout_unit_test' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

## Trimming and AOT

`WolverineFx.AI` is trim and AOT compatible, on one condition: the application has to tell it about its
response types twice — once for type resolution, once for JSON.

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

`RegisterResponseType<T>()` is what turns the identifier a persisted callout carries back into a `Type`
without `Type.GetType`. The `JsonSerializerContext` is what lets the response schema and the answer's
deserialization run off source-generated metadata rather than reflection — `CreateJsonSchema` builds the
schema from whatever type info the options resolve, so no separate schema generator is needed.

Both are optional outside a trimmed application, where the message type registry and `Type.GetType` cover
the same ground. Registering them anyway costs a line each and makes a failure loud at startup rather
than quiet in a dead letter.

One authoring surface has no AOT-clean form, because serializing an arbitrary object needs reflection
over its shape:

```csharp
// warns IL2026 / IL3050 under trim analysis
LlmCallout.Ask<IncidentTriage>(prompt, incident.Snapshot());

// the trim-clean equivalent
LlmCallout.Ask<IncidentTriage>(prompt)
    .WithContext(incident.Snapshot(), AiJsonContext.Default.IncidentSnapshot);
```

`WithContext(string)` takes JSON you produced yourself, which is also the escape hatch when the context
is not a CLR object at all — a rendered document, a retrieved passage, a diff.

::: warning
A `JsonSerializerContext` that does not cover a response type does not throw. `CreateJsonSchema` returns
an *empty* schema, and the model is handed a constraint that constrains nothing — it will usually still
answer, and the answer will usually still parse, so the failure looks like a quality problem rather than
a configuration one. If structured answers go vague after a trimmed publish, check the context first.
:::

## Why the Callout is Not Generic

`LlmCallout.Ask<TResponse>(...)` is generic but `LlmCallout` itself is not, and the response type rides on the
message as data. That is a deliberate choice, recorded in [GH-4227](https://github.com/JasperFx/wolverine/issues/4227).

A callout has to survive being written to a durable inbox and read back after a process restart. Wolverine's
message type registry is a flat name lookup, so a closed `LlmCallout<IncidentTriage>` recovered on a cold start
would have no route back to a `Type` unless every response type in the application had been enumerated at
bootstrap — and the recovery sweep runs before the application has published a callout of its own. The type
parameter also carries no behaviour: its whole job is to name a type twice, once for the schema and once for the
deserialization.

One message type means one handler chain, one dead letter identity, one queue, and one place for the budget
middleware. The typing that actually matters — the handler that receives the answer — is untouched.

## Not In Scope

* **Agents, tools, and model loops.** Tier 2, tracked in [GH-4226](https://github.com/JasperFx/wolverine/issues/4226).
* **Embedding generation.** `IEmbeddingGenerator<,>` is the obvious seam for event store integrations, but it is
  separate adapter work.
