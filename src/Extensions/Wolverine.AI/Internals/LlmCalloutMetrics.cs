using System.Diagnostics.Metrics;
using Microsoft.Extensions.AI;

namespace Wolverine.AI.Internals;

/// <summary>
/// Token and duration metrics for LLM callouts, on their own meter so that spend can be alerted on
/// without pulling in every Wolverine message metric.
///
/// <para>
/// Deliberately thin: <c>IChatClient</c>'s own <c>UseOpenTelemetry</c> middleware already emits the
/// GenAI semantic convention spans and metrics, and an application that wants the full picture should
/// use it. What is here is the piece that middleware cannot see — the Wolverine side labelling that
/// ties spend back to a callout's <see cref="LlmCallout.Tag" />.
/// </para>
/// </summary>
internal static class LlmCalloutMetrics
{
    public const string MeterName = "Wolverine.AI";

    private static readonly Meter _meter = new(MeterName);

    private static readonly Counter<long> _inputTokens =
        _meter.CreateCounter<long>("wolverine.ai.callout.input_tokens", "{token}",
            "Input tokens consumed by Wolverine LLM callouts");

    private static readonly Counter<long> _outputTokens =
        _meter.CreateCounter<long>("wolverine.ai.callout.output_tokens", "{token}",
            "Output tokens produced for Wolverine LLM callouts");

    private static readonly Counter<long> _totalTokens =
        _meter.CreateCounter<long>("wolverine.ai.callout.total_tokens", "{token}",
            "Total tokens billed for Wolverine LLM callouts");

    public static void Record(LlmCallout callout, ChatResponse response, long total)
    {
        var tag = new KeyValuePair<string, object?>("wolverine.ai.tag", callout.Tag ?? "untagged");
        var model = new KeyValuePair<string, object?>("gen_ai.response.model", response.ModelId ?? "unspecified");

        if (response.Usage?.InputTokenCount is { } input) _inputTokens.Add(input, tag, model);
        if (response.Usage?.OutputTokenCount is { } output) _outputTokens.Add(output, tag, model);
        if (total > 0) _totalTokens.Add(total, tag, model);
    }
}
