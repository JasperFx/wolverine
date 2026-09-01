using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using Microsoft.Extensions.AI;

namespace Wolverine.AI.Testing;

/// <summary>
/// A scripted <see cref="IChatClient" /> for tests, so that a callout's whole round trip — the outbox
/// enrolment, the queue, the handler, the published answer — can be exercised without a model, a key,
/// or a network.
///
/// <para>
/// Answers are queued in order and handed out one per request. An exhausted script is an error rather
/// than a repeat, because a test that quietly reuses the last answer for a callout it did not know it
/// was making is a test that passes for the wrong reason.
/// </para>
/// </summary>
/// <example>
/// <code>
/// var chat = new StubChatClient()
///     .Respond(new IncidentTriage("high", "page the on-call"));
///
/// using var host = await Host.CreateDefaultBuilder()
///     .UseWolverine(opts =>
///     {
///         opts.Services.AddSingleton&lt;IChatClient&gt;(chat);
///         opts.AddLlmCallouts();
///     }).StartAsync();
/// </code>
/// </example>
[SuppressMessage("Trimming", "IL2026", Justification = "Test double; serializes a response object supplied by the test itself")]
[SuppressMessage("AOT", "IL3050", Justification = "Test double; serializes a response object supplied by the test itself")]
public class StubChatClient : IChatClient
{
    private readonly Queue<Func<ChatRequest, CancellationToken, Task<ChatResponse>>> _script = new();
    private readonly List<ChatRequest> _requests = new();
    private readonly object _lock = new();

    /// <summary>
    /// Every request this client has been asked to answer, in order. Assert on
    /// <c>chat.Requests.Single().Prompt</c> to check what was actually sent to the model.
    /// </summary>
    public IReadOnlyList<ChatRequest> Requests
    {
        get
        {
            lock (_lock) return _requests.ToArray();
        }
    }

    /// <summary>
    /// Serializer options used to render <see cref="Respond{T}" /> answers. Match these to
    /// <c>LlmCalloutOptions.JsonSerializerOptions</c> if the application overrode them.
    /// </summary>
    public JsonSerializerOptions JsonSerializerOptions { get; set; } = AIJsonUtilities.DefaultOptions;

    /// <summary>
    /// Model id reported on every response, so that tests asserting on logged or measured model names
    /// have something stable to assert against.
    /// </summary>
    public string ModelId { get; set; } = "stub-model";

    /// <summary>
    /// Token counts reported on every response. Set these to drive
    /// <c>LlmBudget.MaximumTokensPerWindow</c> in a test.
    /// </summary>
    public long InputTokenCount { get; set; } = 1;

    /// <summary>
    /// Token counts reported on every response. See <see cref="InputTokenCount" />.
    /// </summary>
    public long OutputTokenCount { get; set; } = 1;

    /// <summary>
    /// Queue a structured answer. The object is serialized exactly as a real provider would return it,
    /// so the executor's deserialization is genuinely exercised rather than bypassed.
    /// </summary>
    public StubChatClient Respond<T>(T response)
    {
        var json = JsonSerializer.Serialize(response, JsonSerializerOptions);
        return Respond(json);
    }

    /// <summary>
    /// Queue a raw text answer. Also how to script a malformed structured answer, to prove that a
    /// handler's failure path dead letters the way it should.
    /// </summary>
    public StubChatClient Respond(string text)
    {
        lock (_lock) _script.Enqueue((_, _) => Task.FromResult(BuildResponse(text)));
        return this;
    }

    /// <summary>
    /// Queue an answer computed from the request, for a test that makes several callouts and needs each
    /// answer to match its prompt.
    /// </summary>
    public StubChatClient Respond(Func<ChatRequest, string> respond)
    {
        lock (_lock) _script.Enqueue((request, _) => Task.FromResult(BuildResponse(respond(request))));
        return this;
    }

    /// <summary>
    /// Queue an answer that takes <paramref name="delay" /> to arrive, honouring the cancellation token
    /// the way a real provider's HTTP client does. This is how to exercise
    /// <c>LlmCalloutOptions.Timeout</c> — and the reason the stub does not simply block: a client that
    /// ignores its token cannot be timed out, so a stub that ignored it would prove the timeout works
    /// when it does not.
    /// </summary>
    public StubChatClient RespondAfter(TimeSpan delay, string text)
    {
        lock (_lock)
        {
            _script.Enqueue(async (_, token) =>
            {
                await Task.Delay(delay, token).ConfigureAwait(false);
                return BuildResponse(text);
            });
        }

        return this;
    }

    /// <summary>
    /// Queue a structured answer that takes <paramref name="delay" /> to arrive. See
    /// <see cref="RespondAfter(TimeSpan, string)" />.
    /// </summary>
    public StubChatClient RespondAfter<T>(TimeSpan delay, T response)
    {
        return RespondAfter(delay, JsonSerializer.Serialize(response, JsonSerializerOptions));
    }

    /// <summary>
    /// Queue a failure, to exercise the retry and dead letter paths.
    /// </summary>
    public StubChatClient Throw(Exception exception)
    {
        lock (_lock) _script.Enqueue((_, _) => throw exception);
        return this;
    }

    public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var request = new ChatRequest(messages.ToArray(), options);

        Func<ChatRequest, CancellationToken, Task<ChatResponse>> next;
        lock (_lock)
        {
            _requests.Add(request);

            if (_script.Count == 0)
            {
                throw new InvalidOperationException(
                    $"{nameof(StubChatClient)} has no scripted answer left for request {_requests.Count}: " +
                    $"\"{Truncate(request.Prompt)}\". Queue one with Respond(...), or assert on why the " +
                    "application made a callout the test did not expect.");
            }

            next = _script.Dequeue();
        }

        return next(request, cancellationToken);
    }

    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
        ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        // Streaming belongs to the agents tier (GH-4226). A one shot callout never asks for it, so an
        // honest failure beats a stub that pretends.
        throw new NotSupportedException(
            $"{nameof(StubChatClient)} does not script streaming responses. LLM callouts are one shot and " +
            "never stream.");
    }

    public object? GetService(Type serviceType, object? serviceKey = null)
    {
        return serviceKey is null && serviceType.IsInstanceOfType(this) ? this : null;
    }

    public void Dispose()
    {
    }

    private ChatResponse BuildResponse(string text)
    {
        return new ChatResponse(new ChatMessage(ChatRole.Assistant, text))
        {
            ModelId = ModelId,
            Usage = new UsageDetails
            {
                InputTokenCount = InputTokenCount,
                OutputTokenCount = OutputTokenCount,
                TotalTokenCount = InputTokenCount + OutputTokenCount
            }
        };
    }

    private static string Truncate(string text)
    {
        return text.Length <= 120 ? text : text[..120] + "...";
    }
}

/// <summary>
/// One request captured by <see cref="StubChatClient" />.
/// </summary>
/// <param name="Messages">The chat messages sent. A callout always sends exactly one user message.</param>
/// <param name="Options">The options sent, carrying the model id, temperature, system prompt, and the JSON schema derived from the callout's response type.</param>
public record ChatRequest(IReadOnlyList<ChatMessage> Messages, ChatOptions? Options)
{
    /// <summary>
    /// The composed user message — the callout's prompt with its JSON context appended.
    /// </summary>
    public string Prompt => string.Join(Environment.NewLine, Messages.Select(x => x.Text));

    /// <summary>
    /// The system prompt sent with this request, if any.
    /// </summary>
    public string? SystemPrompt => Options?.Instructions;

    /// <summary>
    /// Did this request ask the model for a structured answer? False for the text flavour.
    /// </summary>
    public bool IsStructured => Options?.ResponseFormat is ChatResponseFormatJson;
}
