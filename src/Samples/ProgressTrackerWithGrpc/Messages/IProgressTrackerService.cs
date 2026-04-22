using System.ServiceModel;
using ProtoBuf;
using ProtoBuf.Grpc;
using Wolverine.Grpc;

namespace ProgressTrackerWithGrpc.Messages;

/// <summary>
///     Code-first gRPC contract for a job-progress tracker. The single
///     server-streaming method accepts a job description and yields one
///     <see cref="JobProgress"/> item per completed step until the job finishes
///     or the caller cancels.
/// </summary>
[ServiceContract]
[WolverineGrpcService]
public interface IProgressTrackerService
{
    IAsyncEnumerable<JobProgress> RunJob(RunJobRequest request, CallContext context = default);
}

[ProtoContract]
public class RunJobRequest
{
    [ProtoMember(1)] public string JobName { get; set; } = string.Empty;
    [ProtoMember(2)] public int Steps { get; set; }
    [ProtoMember(3)] public int StepDelayMs { get; set; }
}

[ProtoContract]
public class JobProgress
{
    [ProtoMember(1)] public int Step { get; set; }
    [ProtoMember(2)] public int TotalSteps { get; set; }
    [ProtoMember(3)] public int PercentComplete { get; set; }
    [ProtoMember(4)] public string Message { get; set; } = string.Empty;
}
