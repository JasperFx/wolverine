using System.Diagnostics;
using PingPongWithGrpc.Messages;
using PingPongWithGrpcStreaming.Messages;
using Shouldly;
using Xunit;

namespace Wolverine.Grpc.Tests;

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
        capture.AssertRequestActivityChainedUnderServerHostingActivity<PingRequest>();
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
        capture.AssertRequestActivityChainedUnderServerHostingActivity<PingStreamRequest>();
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
    private readonly object _sync = new();
    private readonly List<Activity> _all = new();
    private readonly List<Activity> _wolverine = new();

    public WolverineActivityCapture()
    {
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
                // ActivityStopped fires on whatever thread completed the activity — including
                // Wolverine's runtime threads and ASP.NET Core's request pipeline. A plain
                // List<T>.Add would race with the snapshot reads taken by the assertion helper
                // (and with interpolated-string message construction on Shouldly assertions,
                // which is eager), so all access funnels through a single lock.
                lock (_sync)
                {
                    _all.Add(activity);
                    if (activity.Source.Name == "Wolverine")
                    {
                        _wolverine.Add(activity);
                    }
                }
            }
        };

        ActivitySource.AddActivityListener(_listener);
    }

    public IReadOnlyList<Activity> WolverineActivities
    {
        get
        {
            lock (_sync) return _wolverine.ToArray();
        }
    }

    public IReadOnlyList<Activity> AllActivities
    {
        get
        {
            lock (_sync) return _all.ToArray();
        }
    }

    /// <summary>
    ///     Core M6 guarantee: whatever activity ASP.NET Core's hosting diagnostics establishes for
    ///     the inbound gRPC request, the Wolverine handler activity started during that request
    ///     must share the same TraceId. That proves the gRPC adapter honours Activity.Current so
    ///     downstream consumers (OTel exporters, logs correlated on TraceId) see a single trace.
    ///
    ///     The <see cref="ActivityListener"/> installed by this capture is process-wide, so in a
    ///     parallel xUnit run it also sees activities from other test collections' fixtures and
    ///     from Wolverine background work (e.g. <c>wolverine.stopping.listener</c>). Anchoring the
    ///     assertion on the message type the caller just sent picks out exactly the activity pair
    ///     tied to this test's request and ignores the rest.
    ///
    ///     Match happens on the <c>messaging.message_type</c> tag (from <c>Envelope.WriteTags</c>)
    ///     rather than on <see cref="Activity.OperationName"/>: unary handlers name their span after
    ///     the message type, but streaming handlers use the literal <c>wolverine.streaming</c> span
    ///     name — the tag is the one consistent identifier across both shapes.
    /// </summary>
    public void AssertRequestActivityChainedUnderServerHostingActivity<TMessage>()
    {
        var expectedMessageType = typeof(TMessage).FullName!;

        var wolverineActivity = WolverineActivities.FirstOrDefault(a =>
            a.GetTagItem("messaging.message_type") as string == expectedMessageType);
        wolverineActivity.ShouldNotBeNull(
            $"No Wolverine activity captured with messaging.message_type={expectedMessageType}. All: {DiagnosticDump()}");

        var hosting = AllActivities.FirstOrDefault(a =>
            a.Source.Name == "Microsoft.AspNetCore"
            && a.OperationName == "Microsoft.AspNetCore.Hosting.HttpRequestIn"
            && a.TraceId == wolverineActivity.TraceId);
        hosting.ShouldNotBeNull(
            $"No server-side hosting activity sharing TraceId {wolverineActivity.TraceId} with Wolverine activity for {expectedMessageType}. All: {DiagnosticDump()}");
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
