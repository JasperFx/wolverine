using JasperFx.CodeGeneration;
using Microsoft.Extensions.Logging;
using Wolverine.Runtime.Handlers;

namespace Wolverine.Attributes;

/// <summary>
/// Override Wolverine logging levels 
/// </summary>
public class WolverineLoggingAttribute : ModifyHandlerChainAttribute
{
    private readonly bool _telemetryEnabled;
    private readonly LogLevel _successLogLevel;
    private readonly LogLevel _executionLogLevel;

    /// <summary>
    /// 
    /// </summary>
    /// <param name="telemetryEnabled">Is Open Telemetry tracking enabled on IMessageBus.InvokeAsync()?</param>
    /// <param name="successLogLevel">LogLevel for successful message processing</param>
    /// <param name="executionLogLevel">LogLevel for when Wolverine starts or finishes executing a message</param>
    public WolverineLoggingAttribute(bool telemetryEnabled = true, LogLevel successLogLevel = LogLevel.Information, LogLevel executionLogLevel = LogLevel.Debug)
    {
        _telemetryEnabled = telemetryEnabled;
        _successLogLevel = successLogLevel;
        _executionLogLevel = executionLogLevel;
    }

    public override void Modify(HandlerChain chain, GenerationRules rules)
    {
        chain.TelemetryEnabled = _telemetryEnabled;
        chain.SuccessLogLevel = _successLogLevel;
        chain.ProcessingLogLevel = _executionLogLevel;
    }
}