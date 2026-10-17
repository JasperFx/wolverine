using System.Text;
using Bobcat;
using Bobcat.Engine;

namespace Wolverine.ComplianceTests.Telemetry;

/// <summary>
///     Attaches the whole, unfolded transcript -- every evaluation with every agent's line, including the ones
///     that changed nothing -- to the step that failed. The timeline is what a reader reads; this is what they
///     grep, and what an agent diffs against a green run's.
/// </summary>
/// <remarks>
///     <para>
///         Registered once (<see cref="SpecAttachment.RegisterFailureCapture" />) and called by Bobcat for the
///         innermost failed or errored step only. It finds this scenario's telemetry through the timeline report the
///         scenario already carries: <see cref="ScenarioReports.Current" /> is <c>AsyncLocal</c>, and a capture runs
///         with the test's execution context, so no static registry of "the current test" is needed -- which would
///         be wrong anyway when provider suites run in parallel.
///     </para>
///     <para>
///         Kept under <see cref="SpecAttachment.MaxBytes" /> by <see cref="AgentAssignmentTelemetry.MaxEvents" />: an
///         attachment over the cap is kept as a reference marked omitted, never truncated, so an unbounded transcript
///         would lose ALL of it exactly when a run went long.
///     </para>
/// </remarks>
public sealed class ClusterTranscriptCapture : IFailureCapture
{
    public static readonly ClusterTranscriptCapture Instance = new();

    public Task<IReadOnlyList<CapturedAttachment>> CaptureAsync(FailedStep step, CancellationToken cancellation)
    {
        var timeline = ScenarioReports.Current?.Reports.OfType<AgentAssignmentTimeline>().FirstOrDefault();
        if (timeline is null) return Task.FromResult<IReadOnlyList<CapturedAttachment>>([]);

        var transcript = timeline.Telemetry.Transcript();
        return Task.FromResult<IReadOnlyList<CapturedAttachment>>(
            [new CapturedAttachment("cluster-transcript.txt", Encoding.UTF8.GetBytes(transcript), "text/plain")]);
    }
}
