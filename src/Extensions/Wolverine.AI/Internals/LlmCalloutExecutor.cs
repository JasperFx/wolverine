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

internal class LlmCalloutExecutor : ILlmCalloutExecutor
{
    private readonly IChatClient _client;
    private readonly LlmCalloutOptions _options;
    private readonly ILlmBudgetLedger _ledger;
    private readonly ILogger<LlmCalloutExecutor> _logger;
    private readonly IWolverineRuntime _runtime;
    private readonly LlmResponseBinder _binder;

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
        _binder = new LlmResponseBinder(options);
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
                _binder.SchemaFor(responseType),
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
            // JsonSerializer.Deserialize(string, Type, JsonSerializerOptions) carries
            // [RequiresUnreferencedCode] / [RequiresDynamicCode]; the JsonTypeInfo overload does not.
            // Resolving the JsonTypeInfo out of the options is what lets an AOT application supply a
            // source generated JsonSerializerContext and have this whole path be trim clean, while a
            // reflection-based application sees no difference at all. See GH-4230.
            return _binder.Deserialize(text, responseType)
                   ?? throw new LlmCalloutException(
                       $"The model returned a JSON null for {callout}, which expects {responseType.FullNameInCode()}")
                   {
                       RawResponse = text
                   };
        }
        catch (NotSupportedException e)
        {
            throw new LlmCalloutException(
                $"No JSON type information is available for {responseType.FullNameInCode()}, the response type of " +
                $"{callout}. In a trimmed or AOT application, add it to a JsonSerializerContext and assign that " +
                "context to LlmCalloutOptions.JsonSerializerOptions.TypeInfoResolver.", e)
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

    /// <summary>
    /// Turn the identifier written by <see cref="LlmCallout.IdentifierFor" /> back into a
    /// <see cref="Type" />. Wolverine's message type registry first, so a response type carrying
    /// <c>[MessageIdentity]</c> resolves under its stable alias; then the assembly qualified name, which
    /// is what everything else is stored as and which needs nothing to have been registered.
    /// </summary>
    private Type ResolveResponseType(string identifier)
    {
        if (_responseTypes.TryFind(identifier, out var cached)) return cached;

        // Explicit registrations first, and they are the only step a trimmed or AOT application can
        // rely on: the two below both need metadata the trimmer cannot see from any static call site.
        if (!_binder.TryResolveRegisteredType(identifier, out var resolved))
        {
            resolved = _runtime.Options.HandlerGraph.TryFindMessageType(identifier, out var registered)
                ? registered
                : ResolveReflectively(identifier);
        }

        if (resolved == null)
        {
            throw new LlmCalloutException(
                $"Cannot resolve the response type '{identifier}' for an LLM callout. Either the type has been " +
                "renamed, moved to a different assembly, or removed since this callout was persisted, or this is " +
                "a trimmed application that has not registered it. Register response types with " +
                "ai.RegisterResponseType<T>(), and use [MessageIdentity] on ones that need to survive a rename.");
        }

        _responseTypes = _responseTypes.AddOrUpdate(identifier, resolved);

        return resolved;
    }

    /// <summary>
    /// Last resort for a callout whose response type was never registered. Kept deliberately narrow so
    /// that the registered path above stays trim clean and the AOT smoke gate has something honest to
    /// prove; an application that registers its response types never reaches this.
    /// </summary>
    [UnconditionalSuppressMessage("Trimming", "IL2057",
        Justification = "Reflective fallback for a response type the application did not register. Trimmed and AOT applications must call ai.RegisterResponseType<T>(), which is consulted first and needs no reflection; a type trimmed away here simply fails to resolve and the callout is dead lettered with an actionable message. See the AOT guide and GH-4230.")]
    private static Type? ResolveReflectively(string identifier)
    {
        return Type.GetType(identifier, throwOnError: false);
    }
}
