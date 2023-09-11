using System.Diagnostics;

namespace Wolverine.Runtime.Tracing;

public class WolverineTracingOptions
{
    /// <summary>
    /// Gets or sets an action to enrich an Activity.
    /// </summary>
    /// <remarks>
    /// <para><see cref="Activity"/>: the activity being enriched.</para>
    /// <para>string: the name of the event. Will be one of the constants in <see cref="WolverineEnrichEventNames"/>.
    /// </para>
    /// <para>object: the raw <see cref="Envelope"/> from which additional information can be extracted to enrich the activity.
    /// </para>
    /// </remarks>
    public Action<Activity, string, Envelope>? Enrich { get; set; }
    /// <summary>
    /// Gets or sets a Filter function to filter instrumentation for requests on a per envelope basis.
    /// The Filter gets the <see cref="Envelope"/>, and should return a boolean.
    /// If Filter returns <c>true</c>, the request is collected.
    /// If Filter returns <c>false</c> or throw exception, the request is filtered out.
    /// </summary>
    public Func<Envelope, bool>? SendEnvelopeFilter { get; set; }
    /// <inheritdoc cref="SendEnvelopeFilter"/>
    public Func<Envelope, bool>? ReceiveEnvelopeFilter { get; set; }
    /// <inheritdoc cref="SendEnvelopeFilter"/>
    public Func<Envelope, bool>? ExecuteEnvelopeFilter { get; set; }
    /// <summary>
    /// Gets or sets a Filter function that will be applied to all supported Activity types
    /// The Filter gets the <see cref="Envelope"/>, and should return a boolean.
    /// If Filter returns <c>true</c>, the request is collected.
    /// If Filter returns <c>false</c> or throw exception, the request is filtered out.
    /// </summary>
    public Func<Envelope, bool>? GlobalFilter { get; set; }

    internal WolverineTracingFilter GetFilterForActivity(string activityName)
    {
        Func<Envelope, bool>? activityFilter = null;
        switch (activityName)
        {
            case WolverineActivitySource.SendEnvelopeActivityName:
                activityFilter = SendEnvelopeFilter;
                break;
            case WolverineActivitySource.ReceiveEnvelopeActivityName:
                activityFilter = ReceiveEnvelopeFilter;
                break;
            case WolverineActivitySource.ExecuteEnvelopeActivityName:
                activityFilter = ExecuteEnvelopeFilter;
                break;
        }

        return env => GlobalFilter?.Invoke(env) != false && activityFilter?.Invoke(env) != false;
    }
}

internal delegate bool WolverineTracingFilter(Envelope envelope);