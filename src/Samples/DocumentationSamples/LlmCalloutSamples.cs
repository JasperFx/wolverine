using JasperFx.Events;
using JasperFx.Events.Projections;
using Marten;
using Marten.Events.Aggregation;
using Marten.Events.Projections;
using Wolverine.AI;

namespace DocumentationSamples;

public record IncidentEscalated(string Reason);

public record IncidentResolved;

public record IncidentTriage(string Severity, string RecommendedAction);

public class Incident
{
    public Guid Id { get; set; }
    public int Version { get; set; }
    public string Summary { get; set; } = string.Empty;
    public bool IsEscalated { get; set; }

    public void Apply(IncidentEscalated _) => IsEscalated = true;
    public void Apply(IncidentResolved _) => IsEscalated = false;
}

#region sample_llm_callout_from_a_projection

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

#endregion

#region sample_llm_callout_projection_answer

public static class IncidentTriageHandler
{
    public static void Handle(IncidentTriage triage)
    {
        // page someone, open a ticket, whatever the severity calls for
    }
}

#endregion
