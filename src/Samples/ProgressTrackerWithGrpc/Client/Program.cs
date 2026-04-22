using Grpc.Core;
using Grpc.Net.Client;
using ProgressTrackerWithGrpc.Messages;
using ProtoBuf.Grpc.Client;

// Allow plain HTTP/2 for the local sample (no TLS cert required).
AppContext.SetSwitch("System.Net.Http.SocketsHttpHandler.Http2UnencryptedSupport", true);

using var channel = GrpcChannel.ForAddress("http://localhost:5009");
var tracker = channel.CreateGrpcService<IProgressTrackerService>();

// --- Full job: all steps complete ---
Console.WriteLine("=== Full job (10 steps) ===");
await foreach (var p in tracker.RunJob(new RunJobRequest { JobName = "build", Steps = 10, StepDelayMs = 100 }))
{
    Console.WriteLine($"  [{p.PercentComplete,3}%] {p.Message}");
}
Console.WriteLine("Job complete.\n");

// --- Cancelled job: client cancels after step 3 ---
Console.WriteLine("=== Cancelled job (client cancels at step 3) ===");
using var cts = new CancellationTokenSource();
try
{
    await foreach (var p in tracker.RunJob(
                       new RunJobRequest { JobName = "deploy", Steps = 10, StepDelayMs = 100 }, cts.Token))
    {
        Console.WriteLine($"  [{p.PercentComplete,3}%] {p.Message}");
        if (p.Step == 3) cts.Cancel();
    }
}
catch (RpcException ex) when (ex.StatusCode == StatusCode.Cancelled)
{
    Console.WriteLine("  Job cancelled by client.");
}
