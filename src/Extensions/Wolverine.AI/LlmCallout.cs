using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using JasperFx.Core;
using JasperFx.Core.Reflection;
using Microsoft.Extensions.AI;
using Wolverine.Attributes;
using Wolverine.Util;

namespace Wolverine.AI;

/// <summary>
/// A request for a one shot large language model completion, modeled as an ordinary Wolverine message
/// so that it inherits the outbox, retries, error policies, back pressure, and observability that every
/// other message already has.
///
/// <para>
/// Build one with <see cref="Ask{TResponse}(string)" /> and return it from a message handler, an HTTP
/// endpoint, or a projection's <c>RaiseSideEffects</c>. The answer comes back as a separate, ordinary
/// message of type <c>TResponse</c> — this is publish only, there is no correlated reply.
/// </para>
/// </summary>
/// <remarks>
/// This type is deliberately NOT generic. The response type rides on the message as data rather than as
/// a type parameter because the callout has to survive a durable inbox round trip:
/// <c>HandlerGraph.TryFindMessageType</c> is a flat lookup, so a closed <c>LlmCallout&lt;T&gt;</c>
/// recovered after a process restart would have no route back to a <see cref="Type" /> unless every
/// response type had been enumerated at bootstrap. One message type also means one handler chain, one
/// dead letter identity, and one place for the budget middleware. See GH-4227.
/// </remarks>
[LocalQueue(QueueName)]
public sealed class LlmCallout
{
    /// <summary>
    /// Name of the dedicated local queue that every callout is executed on. Configure it with
    /// <c>LlmCalloutOptions.ConfigureQueue</c>, or directly through
    /// <c>opts.LocalQueue(LlmCallout.QueueName)</c>.
    /// </summary>
    public const string QueueName = "llm-callouts";

    /// <summary>
    /// The prompt sent to the model as the user message. Application owned — Wolverine.AI is not a
    /// prompt template framework.
    /// </summary>
    public string Prompt { get; set; } = string.Empty;

    /// <summary>
    /// Optional JSON context appended underneath <see cref="Prompt" /> in the user message. Set by the
    /// <c>context</c> overloads of <see cref="Ask{TResponse}(string, object)" />, which serialize the
    /// supplied object with the same options used to build the response schema.
    /// </summary>
    public string? Context { get; set; }

    /// <summary>
    /// Optional system prompt, sent as <see cref="ChatOptions.Instructions" />. Falls back to
    /// <c>LlmCalloutOptions.DefaultSystemPrompt</c> when null.
    /// </summary>
    public string? SystemPrompt { get; set; }

    /// <summary>
    /// Identifies the CLR type the answer is deserialized into and published as. Null means the text
    /// flavour, which publishes an <see cref="LlmTextResponse" /> instead.
    ///
    /// <para>
    /// This is an assembly qualified name (<c>Namespace.Type, Assembly</c>) so that a callout recovered
    /// from a durable inbox on a cold start resolves without depending on anything having been
    /// registered first. A response type carrying <c>[MessageIdentity]</c> is stored under that stable
    /// alias instead, and resolved through Wolverine's message type registry.
    /// </para>
    /// </summary>
    public string? ResponseType { get; set; }

    /// <summary>
    /// Overrides <c>LlmCalloutOptions.DefaultModelId</c> for this one callout.
    /// </summary>
    public string? ModelId { get; set; }

    /// <summary>
    /// Sampling temperature passed straight through to <see cref="ChatOptions.Temperature" />.
    /// </summary>
    public float? Temperature { get; set; }

    /// <summary>
    /// Caps the model's output, passed through to <see cref="ChatOptions.MaxOutputTokens" />.
    /// </summary>
    public int? MaxOutputTokens { get; set; }

    /// <summary>
    /// A short, stable label for what this callout is <i>for</i> — "triage", "summarize", "classify".
    /// Carried onto the metrics and log entries the executor emits, and onto
    /// <see cref="LlmTextResponse" />, which is how a text flavour handler tells one kind of callout
    /// from another.
    /// </summary>
    public string? Tag { get; set; }

    /// <summary>
    /// A logical identity for this callout, stamped onto the outgoing envelope's
    /// <see cref="Envelope.DeduplicationId" />. Use it where the same callout can legitimately be
    /// published more than once — a projection's <c>RaiseSideEffects</c> replayed by a daemon retry,
    /// say, where stream id + version is the natural key. Carrying the id does not enforce anything on
    /// its own; turn on <c>LlmCalloutOptions.DeduplicateCallouts</c> plus
    /// <c>opts.Durability.EnableMessageDeduplication</c> for that.
    /// </summary>
    [DeduplicationIdentity]
    public string? DeduplicationId { get; set; }

    /// <summary>
    /// Ask the model for a structured answer. The answer is deserialized into
    /// <typeparamref name="TResponse" /> and published as an ordinary message.
    /// </summary>
    public static LlmCallout Ask<TResponse>(string prompt)
    {
        return new LlmCallout { Prompt = prompt, ResponseType = IdentifierFor(typeof(TResponse)) };
    }

    /// <summary>
    /// Ask the model for a structured answer, with <paramref name="context" /> serialized to JSON and
    /// appended underneath the prompt.
    /// </summary>
    /// <remarks>
    /// Serializing an arbitrary object needs reflection over its shape, which is why this overload is
    /// annotated. In a trimmed or AOT application use <see cref="Ask{TResponse}(string)" /> and then
    /// <see cref="WithContext{TContext}(TContext, JsonTypeInfo{TContext})" />, which serializes through
    /// source generated type info instead.
    /// </remarks>
    [RequiresUnreferencedCode("Serializes an arbitrary context object. Use WithContext(context, typeInfo) in trimmed applications.")]
    [RequiresDynamicCode("Serializes an arbitrary context object. Use WithContext(context, typeInfo) in trimmed applications.")]
    public static LlmCallout Ask<TResponse>(string prompt, object context)
    {
        return new LlmCallout
        {
            Prompt = prompt,
            Context = Serialize(context),
            ResponseType = IdentifierFor(typeof(TResponse))
        };
    }

    /// <summary>
    /// Ask the model for a plain text answer, published as an <see cref="LlmTextResponse" />.
    /// </summary>
    public static LlmCallout Ask(string prompt)
    {
        return new LlmCallout { Prompt = prompt };
    }

    /// <summary>
    /// Ask the model for a plain text answer, with <paramref name="context" /> serialized to JSON and
    /// appended underneath the prompt. Published as an <see cref="LlmTextResponse" />.
    /// </summary>
    /// <remarks>
    /// See <see cref="Ask{TResponse}(string, object)" /> for why this overload is annotated and what a
    /// trimmed application uses instead.
    /// </remarks>
    [RequiresUnreferencedCode("Serializes an arbitrary context object. Use WithContext(context, typeInfo) in trimmed applications.")]
    [RequiresDynamicCode("Serializes an arbitrary context object. Use WithContext(context, typeInfo) in trimmed applications.")]
    public static LlmCallout Ask(string prompt, object context)
    {
        return new LlmCallout { Prompt = prompt, Context = Serialize(context) };
    }

    /// <summary>
    /// Is this callout expecting an answer of type <typeparamref name="TResponse" />? The assertion
    /// that pairs with <c>tracked.Sent.SingleMessage&lt;LlmCallout&gt;()</c> in a tracked session test.
    /// </summary>
    public bool ExpectsResponse<TResponse>()
    {
        return ResponseType == IdentifierFor(typeof(TResponse));
    }

    /// <summary>
    /// Attach context that is already JSON, appended underneath the prompt. The trim clean way to give a
    /// callout context, and the escape hatch when the context is not a CLR object at all — a rendered
    /// document, a retrieved passage, a diff.
    /// </summary>
    public LlmCallout WithContext(string json)
    {
        Context = json;
        return this;
    }

    /// <summary>
    /// Attach context serialized through source generated type info, so the whole callout stays trim and
    /// AOT clean:
    /// <c>LlmCallout.Ask&lt;IncidentTriage&gt;(prompt).WithContext(snapshot, MyAiContext.Default.IncidentSnapshot)</c>.
    /// </summary>
    public LlmCallout WithContext<TContext>(TContext context, JsonTypeInfo<TContext> typeInfo)
    {
        Context = JsonSerializer.Serialize(context, typeInfo);
        return this;
    }

    /// <summary>
    /// Set the system prompt for this callout, overriding <c>LlmCalloutOptions.DefaultSystemPrompt</c>.
    /// </summary>
    public LlmCallout WithSystemPrompt(string systemPrompt)
    {
        SystemPrompt = systemPrompt;
        return this;
    }

    /// <summary>
    /// Run this one callout against a different model than the configured default.
    /// </summary>
    public LlmCallout UsingModel(string modelId)
    {
        ModelId = modelId;
        return this;
    }

    /// <summary>
    /// Set the sampling temperature for this callout.
    /// </summary>
    public LlmCallout WithTemperature(float temperature)
    {
        Temperature = temperature;
        return this;
    }

    /// <summary>
    /// Cap the model's output for this callout.
    /// </summary>
    public LlmCallout WithMaxOutputTokens(int maxOutputTokens)
    {
        MaxOutputTokens = maxOutputTokens;
        return this;
    }

    /// <summary>
    /// Label this callout for metrics, logging, and <see cref="LlmTextResponse" /> dispatch.
    /// </summary>
    public LlmCallout Tagged(string tag)
    {
        Tag = tag;
        return this;
    }

    /// <summary>
    /// Give this callout a logical identity so that a republished duplicate can be recognized. See
    /// <see cref="DeduplicationId" />.
    /// </summary>
    public LlmCallout DeduplicatedBy(string deduplicationId)
    {
        DeduplicationId = deduplicationId;
        return this;
    }

    public override string ToString()
    {
        var expects = ResponseType ?? "text";
        return Tag.IsEmpty()
            ? $"LlmCallout expecting {expects}"
            : $"LlmCallout '{Tag}' expecting {expects}";
    }

    /// <summary>
    /// How a response type is written onto the wire. A type carrying <c>[MessageIdentity]</c> uses that
    /// stable alias — the point of the attribute is that renaming the type does not strand messages
    /// already in flight. Everything else uses the assembly qualified name, which
    /// <see cref="Type.GetType(string)" /> resolves with no registration and no bootstrap scan, cold
    /// start included.
    /// </summary>
    internal static string IdentifierFor(Type responseType)
    {
        if (responseType.HasAttribute<MessageIdentityAttribute>())
        {
            return responseType.ToMessageTypeName();
        }

        return $"{responseType.FullName}, {responseType.Assembly.GetName().Name}";
    }

    /// <summary>
    /// Options for rendering a context object into a prompt. Microsoft.Extensions.AI's own
    /// <see cref="AIJsonUtilities.DefaultOptions" /> matches these except that it sets
    /// <c>WriteIndented</c>, and indentation in a prompt is billed input tokens buying nothing — it also
    /// inflates every character against <see cref="LlmBudget.MaximumPromptCharacters" />. The naming
    /// policy and null handling are kept identical so that what the model is shown does not otherwise
    /// change.
    /// </summary>
    private static readonly JsonSerializerOptions _contextSerialization = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    [RequiresUnreferencedCode("Serializes an arbitrary context object")]
    [RequiresDynamicCode("Serializes an arbitrary context object")]
    private static string Serialize(object context)
    {
        return JsonSerializer.Serialize(context, context.GetType(), _contextSerialization);
    }
}
