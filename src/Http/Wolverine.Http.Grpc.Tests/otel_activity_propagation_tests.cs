using System.Diagnostics;
using Shouldly;
using Xunit;

namespace Wolverine.Http.Grpc.Tests;

/// <summary>
///     M6: verify that Wolverine handler activities chain cleanly under the ASP.NET Core
///     gRPC server activity. This is the boundary Wolverine owns — the gRPC adapter must
///     honour <see cref="Activity.Current"/> so the handler's telemetry joins the same trace
///     as the inbound request. Cross-boundary (client → server) propagation of the W3C
///     <c>traceparent</c> header is ASP.NET Core + HttpClient's responsibility and works
///     over real HTTP/2; <see cref="Microsoft.AspNetCore.TestHost.TestServer"/> bypasses
///     that layer, so these tests assert only the server-side chain we actually own.
/// </summary>
[Collection("grpc")]
public class otel_activity_propagation_tests : IClassFixture<GrpcTestFixture>
{
    private readonly GrpcTestFixture _fixture;

    public otel_activity_propagation_tests(GrpcTestFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task unary_call_wolverine_activity_chains_under_aspnetcore_hosting_activity()
    {
        using var capture = new WolverineActivityCapture();

        var client = _fixture.CreateClient<IPingService>();
        var reply = await client.Ping(new PingRequest { Message = "otel" });

        reply.Echo.ShouldBe("otel");
        capture.AssertWolverineActivityChainedUnderServerHostingActivity();
    }

    [Fact]
    public async Task server_streaming_call_wolverine_activity_chains_under_aspnetcore_hosting_activity()
    {
        using var capture = new WolverineActivityCapture();

        var client = _fixture.CreateClient<IPingStreamService>();

        var count = 0;
        await foreach (var _ in client.PingStream(new PingStreamRequest { Message = "otel", Count = 3 }))
        {
            count++;
        }

        count.ShouldBe(3);
        capture.AssertWolverineActivityChainedUnderServerHostingActivity();
    }
}

/// <summary>
///     Scoped helper that installs a global <see cref="ActivityListener"/> for the duration of a
///     single test and collects every completed <see cref="Activity"/>. Dispose restores the
///     process-wide listener set. Sampling must be <see cref="ActivitySamplingResult.AllData"/> so
///     each captured activity carries a real TraceId (otherwise comparisons are meaningless).
/// </summary>
internal sealed class WolverineActivityCapture : IDisposable
{
    private readonly ActivityListener _listener;

    public WolverineActivityCapture()
    {
        WolverineActivities = new List<Activity>();
        AllActivities = new List<Activity>();

        // Broad sampling is deliberate: Microsoft.AspNetCore.* in particular must be sampled so the
        // server emits a hosting activity, which is the chain root every Wolverine handler hangs
        // under. Narrower filters silently skip the hosting activity and the whole test becomes moot.
        _listener = new ActivityListener
        {
            ShouldListenTo = _ => true,
            Sample = SampleAllData,
            SampleUsingParentId = SampleAllDataByParentId,
            ActivityStopped = activity =>
            {
                AllActivities.Add(activity);
                if (activity.Source.Name == "Wolverine")
                {
                    WolverineActivities.Add(activity);
                }
            }
        };

        ActivitySource.AddActivityListener(_listener);
    }

    public List<Activity> WolverineActivities { get; }

    public List<Activity> AllActivities { get; }

    /// <summary>
    ///     Core M6 guarantee: whatever activity ASP.NET Core's hosting diagnostics establishes for
    ///     the inbound gRPC request, every Wolverine handler activity started during that request
    ///     must share the same TraceId. That proves the gRPC adapter honours Activity.Current so
    ///     downstream consumers (OTel exporters, logs correlated on TraceId) see a single trace.
    /// </summary>
    public void AssertWolverineActivityChainedUnderServerHostingActivity()
    {
        var hosting = AllActivities.FirstOrDefault(a =>
            a.Source.Name == "Microsoft.AspNetCore" && a.OperationName == "Microsoft.AspNetCore.Hosting.HttpRequestIn");

        hosting.ShouldNotBeNull(
            $"No server-side hosting activity captured. All: {DiagnosticDump()}");

        WolverineActivities.ShouldNotBeEmpty($"No Wolverine activity captured. All: {DiagnosticDump()}");
        WolverineActivities.ShouldAllBe(
            a => a.TraceId == hosting.TraceId,
            $"Wolverine activity TraceId did not match server hosting activity ({hosting.TraceId}). All: {DiagnosticDump()}");
    }

    private string DiagnosticDump()
        => Environment.NewLine
           + string.Join(Environment.NewLine,
               AllActivities.Select(a => $"  src={a.Source.Name} op={a.OperationName} trace={a.TraceId}"));

    public void Dispose() => _listener.Dispose();

    private static ActivitySamplingResult SampleAllData(ref ActivityCreationOptions<ActivityContext> _)
        => ActivitySamplingResult.AllData;

    private static ActivitySamplingResult SampleAllDataByParentId(ref ActivityCreationOptions<string> _)
        => ActivitySamplingResult.AllData;
}
