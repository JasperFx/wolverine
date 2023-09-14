using System.Diagnostics;
using Wolverine.Runtime.Agents;

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

    
    /// <inheritdoc cref="GlobalFilter"/>
    /// <remarks>Only applies to spans generated as a result of send events</remarks>
    public Func<Envelope, bool>? SendEnvelopeFilter { get; set; }

    /// <inheritdoc cref="GlobalFilter"/>
    /// <remarks>Only applies to spans generated as a result of receipt events</remarks>
    public Func<Envelope, bool>? ReceiveEnvelopeFilter { get; set; }

    /// <inheritdoc cref="GlobalFilter"/>
    /// <remarks>Only applies to spans generated as a result of envelope execution events</remarks>
    public Func<Envelope, bool>? ExecuteEnvelopeFilter { get; set; }

    /// <summary>
    /// <para>
    /// Gets or sets a Filter function to filter instrumentation for requests on a per envelope basis.
    /// The Filter gets the <see cref="Envelope" />, and should return a boolean.
    /// </para>
    /// <para>
    /// If Filter returns <c>true</c>, the request is collected.
    /// </para>
    /// <para>
    /// If Filter returns <c>false</c> or throws an exception, the request is filtered out.
    /// </para>
    /// </summary>
    public Func<Envelope, bool>? GlobalFilter { get; set; }

    /// <summary>
    /// While enabled, Wolverine will not generate traces or activities for internal message types.
    /// </summary>
    public bool SuppressInternalMessageTypes { get; set; }

    private bool filterInternalMessages(Envelope envelope)
    {
        if (SuppressInternalMessageTypes)
        {
            return envelope.Message != null &&
                   !envelope.Message.GetType().IsAssignableTo(typeof(IInternalMessage));
        }

        return true;
    }

    internal WolverineTracingFilter GetFilterForActivity(string activityName)
    {
        var activityFilter = activityName switch
        {
            WolverineActivitySource.SendEnvelopeActivityName => SendEnvelopeFilter,
            WolverineActivitySource.ReceiveEnvelopeActivityName => ReceiveEnvelopeFilter,
            WolverineActivitySource.ExecuteEnvelopeActivityName => ExecuteEnvelopeFilter,
            _ => null
        };

        return env => filterInternalMessages(env) && 
                      GlobalFilter?.Invoke(env) != false &&
                      activityFilter?.Invoke(env) != false;
    }
}

internal delegate bool WolverineTracingFilter(Envelope envelope);