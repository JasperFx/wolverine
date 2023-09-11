using System.Diagnostics;
using System.Reflection;

namespace Wolverine.Runtime.Tracing;

internal static class WolverineActivitySource
{
    internal static readonly AssemblyName AssemblyName = typeof(WolverineActivitySource).Assembly.GetName();
    internal static readonly string ActivitySourceName = AssemblyName.Name!;
    internal const string SendEnvelopeActivityName = WolverineEnrichEventNames.StartSendEnvelope;
    internal const string ReceiveEnvelopeActivityName = WolverineEnrichEventNames.StartReceivingEnvelope;
    internal const string ExecuteEnvelopeActivityName = WolverineEnrichEventNames.StartExecutingEnvelope;
    private static readonly Version _version = AssemblyName.Version!;
    public static ActivitySource ActivitySource { get; } = new ActivitySource(ActivitySourceName, _version.ToString());
    public static WolverineTracingOptions Options { get; set; } = new WolverineTracingOptions();
}