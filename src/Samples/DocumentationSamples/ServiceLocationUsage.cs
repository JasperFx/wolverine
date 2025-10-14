using JasperFx.CodeGeneration.Model;
using Microsoft.Extensions.Hosting;
using Wolverine;

namespace DocumentationSamples;

public class ServiceLocationUsage
{
    public static void configure()
    {
        #region sample_configuring_ServiceLocationPolicy

        var builder = Host.CreateApplicationBuilder();
        builder.UseWolverine(opts =>
        {
            // This is the default behavior. Wolverine will allow you to utilize
            // service location in the codegen, but will warn you through log messages
            // when this happens
            opts.ServiceLocationPolicy = ServiceLocationPolicy.AllowedButWarn;

            // Tell Wolverine to just be quiet about service location and let it
            // all go. For any of you with small children, I defy you to get the 
            // Frozen song out of your head now...
            opts.ServiceLocationPolicy = ServiceLocationPolicy.AlwaysAllowed;

            // Wolverine will throw exceptions at runtime if it encounters
            // a message handler or HTTP endpoint that would require service
            // location in the code generation
            // Use this option to disallow any undesirably service location
            opts.ServiceLocationPolicy = ServiceLocationPolicy.NotAllowed;
        });

        #endregion
    }
}