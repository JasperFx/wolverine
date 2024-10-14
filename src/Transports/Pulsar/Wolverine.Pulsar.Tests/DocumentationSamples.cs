using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Wolverine.ComplianceTests.Compliance;

namespace Wolverine.Pulsar.Tests;

public static class DocumentationSamples
{
    public static async Task configure()
    {
        #region sample_configuring_pulsar

        var builder = Host.CreateApplicationBuilder();
        builder.UseWolverine(opts =>
        {
            opts.UsePulsar(c =>
            {
                var pulsarUri = builder.Configuration.GetValue<Uri>("pulsar");
                c.ServiceUrl(pulsarUri);
                
                // Any other configuration you want to apply to your
                // Pulsar client
            });

            // Publish messages to a particular Pulsar topic
            opts.PublishMessage<Message1>()
                .ToPulsarTopic("persistent://public/default/one")
                
                // And all the normal Wolverine options...
                .SendInline();

            // Listen for incoming messages from a Pulsar topic
            opts.ListenToPulsarTopic("persistent://public/default/two")
                
                // And all the normal Wolverine options...
                .Sequential();
        });

        #endregion
    }
}