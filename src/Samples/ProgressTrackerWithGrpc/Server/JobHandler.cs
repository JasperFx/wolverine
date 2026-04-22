using System.Runtime.CompilerServices;
using ProgressTrackerWithGrpc.Messages;

namespace ProgressTrackerWithGrpc.Server;

/// <summary>
///     Wolverine handler that simulates a multi-step job and streams back one
///     <see cref="JobProgress"/> update per completed step. The
///     <c>IAsyncEnumerable&lt;T&gt;</c> return shape is Wolverine's server-streaming
///     handler contract; the generated gRPC service forwards via
///     <c>IMessageBus.StreamAsync&lt;T&gt;</c> and passes the caller's cancellation
///     token so mid-stream cancellation propagates cleanly.
/// </summary>
public static class JobHandler
{
    public static async IAsyncEnumerable<JobProgress> Handle(
        RunJobRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        for (var step = 1; step <= request.Steps; step++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Delay(request.StepDelayMs, cancellationToken);

            var pct = (int)(step * 100.0 / request.Steps);
            yield return new JobProgress
            {
                Step = step,
                TotalSteps = request.Steps,
                PercentComplete = pct,
                Message = $"[{request.JobName}] Completed step {step}/{request.Steps}"
            };
        }
    }
}
