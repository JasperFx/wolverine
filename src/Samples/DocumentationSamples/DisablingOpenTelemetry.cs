using Microsoft.Extensions.Hosting;
using Wolverine;
using Wolverine.Transports.Tcp;

namespace DocumentationSamples;

public class DisablingOpenTelemetry
{
    public static async Task bootstrap()
    {
        #region sample_disabling_open_telemetry_by_endpoint

        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts
                    .PublishAllMessages()
                    .ToPort(2222)
                    
                    // Disable Open Telemetry data collection on 
                    // all messages sent, received, or executed
                    // from this endpoint
                    .TelemetryEnabled(false);
            }).StartAsync();

        #endregion
    }
}