using JasperFx;
using Microsoft.Extensions.Hosting;
using Wolverine;

namespace DocumentationSamples;

public class DisablingStorageConstruction
{
    public async Task configure()
    {
        #region sample_disable_auto_build_envelope_storage

        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                // Disable automatic database migrations for message
                // storage
                opts.AutoBuildMessageStorageOnStartup = AutoCreate.None;
            }).StartAsync();

        #endregion
    }
}