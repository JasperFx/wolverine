using JasperFx.CodeGeneration;
using Microsoft.Extensions.Logging;
using Wolverine.Logging;
using Wolverine.Runtime.Handlers;

namespace Wolverine.Attributes;

/// <summary>
/// Override Wolverine logging levels
/// </summary>
public class WolverineLoggingAttribute : ModifyHandlerChainAttribute
{
    private LogLevel? _messageStartingLogLevel;

    /// <summary>
    /// 
    /// </summary>
    /// <param name="telemetryEnabled">Is Open Telemetry tracking enabled on IMessageBus.InvokeAsync()?</param>
    /// <param name="successLogLevel">LogLevel for successful message processing</param>
    /// <param name="executionLogLevel">LogLevel for when Wolverine starts or finishes executing a message</param>
    public WolverineLoggingAttribute(bool telemetryEnabled = true, LogLevel successLogLevel = LogLevel.Information, LogLevel executionLogLevel = LogLevel.Debug)
    {
        TelemetryEnabled = telemetryEnabled;
        SuccessLogLevel = successLogLevel;
        ExecutionLogLevel = executionLogLevel;
    }

    public override void Modify(HandlerChain chain, GenerationRules rules)
    {
        chain.TelemetryEnabled = TelemetryEnabled;
        chain.SuccessLogLevel = SuccessLogLevel;
        chain.ProcessingLogLevel = ExecutionLogLevel;

        if (_messageStartingLogLevel.HasValue)
        {
            // Check if the frame already exists!
            var existing = chain.Middleware.OfType<LogStartingActivity>().FirstOrDefault();
            if (_messageStartingLogLevel.Value == LogLevel.None && existing != null)
            {
                chain.Middleware.Remove(existing);
            }
            else if (existing != null)
            {
                existing.Level = _messageStartingLogLevel.Value;
            }
            else if (_messageStartingLogLevel.Value != LogLevel.None)
            {
                chain.Middleware.Insert(0, new LogStartingActivity(_messageStartingLogLevel.Value, chain));
            }
        }
    }

    public bool TelemetryEnabled { get; set; }

    public LogLevel SuccessLogLevel { get; set; }

    public LogLevel ExecutionLogLevel { get; set; }

    /// <summary>
    /// If set to a value (besides LogLevel.None!), this attribute will direct Wolverine to generate code
    /// logging the beginning of execution of the handler code
    /// </summary>
    public LogLevel MessageStartingLevel {
        set
        {
            _messageStartingLogLevel = value;
        }
        get
        {
            return _messageStartingLogLevel ?? LogLevel.None;
        }
    } 
}