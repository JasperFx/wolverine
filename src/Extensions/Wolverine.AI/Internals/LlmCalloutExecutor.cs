using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using ImTools;
using JasperFx.Core;
using JasperFx.Core.Reflection;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Wolverine.Runtime;

namespace Wolverine.AI.Internals;

/// <summary>
/// Runs a single <see cref="LlmCallout" /> against the registered <see cref="IChatClient" /> and returns
/// whatever should be published as the cascading answer.
/// </summary>
public interface ILlmCalloutExecutor
{
    Task<object> ExecuteAsync(LlmCallout callout, CancellationToken cancellationToken);
}

// The whole class is reflection over a Type carried as a string on the message, which is the reason
// WolverineFx.AI is not marked IsAotCompatible. Suppressions here would claim a guarantee the design
// cannot make; making it trim clean needs source generated schemas and a JsonSerializerContext keyed
// off the registered response types. See the csproj comment and GH-4227.
[RequiresUnreferencedCode("Builds a JSON schema and deserializes an answer for a response type named on the message at runtime")]
[RequiresDynamicCode("Builds a JSON schema and deserializes an answer for a response type named on the message at runtime")]
internal class LlmCalloutExecutor : ILlmCalloutExecutor
{
    private readonly IChatClient _client;
    private readonly LlmCalloutOptions _options;
    private readonly ILlmBudgetLedger _ledger;
    private readonly ILogger<LlmCalloutExecutor> _logger;
    private readonly IWolverineRuntime _runtime;

    // Schema construction walks the response type's properties, which is meaningfully more expensive
    // than the rest of the per callout work. ImHashMap per the repo's hot path convention: lock free,
    // non-allocating reads, copy on write.
    private ImHashMap<Type, JsonElement> _schemas = ImHashMap<Type, JsonElement>.Empty;
    private ImHashMap<string, Type> _responseTypes = ImHashMap<string, Type>.Empty;

    private const string chatModelUnknown = "(unspecified)";

    public LlmCalloutExecutor(IChatClient client, LlmCalloutOptions options, ILlmBudgetLedger ledger,
        ILogger<LlmCalloutExecutor> logger, IWolverineRuntime runtime)
    {
        _client = client;
        _options = options;
        _ledger = ledger;
        _logger = logger;
        _runtime = runtime;
    }

    public async Task<object> ExecuteAsync(LlmCallout callout, CancellationToken cancellationToken)
    {
        var responseType = callout.ResponseType.IsEmpty() ? null : ResolveResponseType(callout.ResponseType!);

        var chatOptions = new ChatOptions
        {
            ModelId = callout.ModelId ?? _options.DefaultModelId,
            Instructions = callout.SystemPrompt ?? _options.DefaultSystemPrompt,
            Temperature = callout.Temperature,
            MaxOutputTokens = callout.MaxOutputTokens
        };

        if (responseType != null)
        {
            chatOptions.ResponseFormat = ChatResponseFormat.ForJsonSchema(
                SchemaFor(responseType),
                schemaName: responseType.Name,
                schemaDescription: $"The structured answer expected for a {callout.Tag ?? "Wolverine"} LLM callout");
        }

        var message = new ChatMessage(ChatRole.User, LlmCalloutPrompt.Compose(callout));

        // The timeout is linked to, not a replacement for, the handler's own token: a shutdown still
        // cancels the call, and a hung provider request surfaces as a Wolverine retry rather than as a
        // handler that never returns.
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_options.Timeout);

        ChatResponse response;
        try
        {
            response = await _client.GetResponseAsync([message], chatOptions, timeout.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"LLM callout {callout} timed out after {_options.Timeout}. Raise LlmCalloutOptions.Timeout, or lower MaxOutputTokens.");
        }

        RecordUsage(callout, response);

        if (responseType == null)
        {
            return new LlmTextResponse(response.Text, callout);
        }

        return Deserialize(callout, responseType, response.Text);
    }

    private object Deserialize(LlmCallout callout, Type responseType, string text)
    {
        if (text.IsEmpty())
        {
            throw new LlmCalloutException(
                $"The model returned an empty response for {callout}, which expects {responseType.FullNameInCode()}")
            {
                RawResponse = text
            };
        }

        try
        {
            return JsonSerializer.Deserialize(text, responseType, _options.JsonSerializerOptions)
                   ?? throw new LlmCalloutException(
                       $"The model returned a JSON null for {callout}, which expects {responseType.FullNameInCode()}")
                   {
                       RawResponse = text
                   };
        }
        catch (JsonException e)
        {
            throw new LlmCalloutException(
                $"The model's answer to {callout} could not be read as {responseType.FullNameInCode()}. " +
                "The raw response is on this exception's RawResponse property.", e)
            {
                RawResponse = text
            };
        }
    }

    private void RecordUsage(LlmCallout callout, ChatResponse response)
    {
        var usage = response.Usage;
        if (usage == null) return;

        var total = usage.TotalTokenCount
                    ?? (usage.InputTokenCount ?? 0) + (usage.OutputTokenCount ?? 0);

        _ledger.Record(total);
        LlmCalloutMetrics.Record(callout, response, total);

        _logger.LogDebug(
            "LLM callout {Tag} answered by {ModelId}: {InputTokens} in, {OutputTokens} out, {TotalTokens} total",
            callout.Tag ?? "(untagged)", response.ModelId ?? chatModelUnknown, usage.InputTokenCount,
            usage.OutputTokenCount, total);
    }

    private JsonElement SchemaFor(Type responseType)
    {
        if (_schemas.TryFind(responseType, out var schema)) return schema;

        schema = AIJsonUtilities.CreateJsonSchema(responseType, serializerOptions: _options.JsonSerializerOptions);
        _schemas = _schemas.AddOrUpdate(responseType, schema);

        return schema;
    }

    /// <summary>
    /// Turn the identifier written by <see cref="LlmCallout.IdentifierFor" /> back into a
    /// <see cref="Type" />. Wolverine's message type registry first, so a response type carrying
    /// <c>[MessageIdentity]</c> resolves under its stable alias; then the assembly qualified name, which
    /// is what everything else is stored as and which needs nothing to have been registered.
    /// </summary>
    private Type ResolveResponseType(string identifier)
    {
        if (_responseTypes.TryFind(identifier, out var cached)) return cached;

        Type? resolved = null;

        if (_runtime.Options.HandlerGraph.TryFindMessageType(identifier, out var registered))
        {
            resolved = registered;
        }

        resolved ??= Type.GetType(identifier, throwOnError: false);

        if (resolved == null)
        {
            throw new LlmCalloutException(
                $"Cannot resolve the response type '{identifier}' for an LLM callout. The type has most likely " +
                "been renamed, moved to a different assembly, or removed since this callout was persisted. " +
                "Use [MessageIdentity] on response types that need to survive being renamed.");
        }

        _responseTypes = _responseTypes.AddOrUpdate(identifier, resolved);

        return resolved;
    }
}
