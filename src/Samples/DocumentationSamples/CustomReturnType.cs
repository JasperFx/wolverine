using JasperFx;
using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Frames;
using JasperFx.Core.Reflection;
using Microsoft.Extensions.Hosting;
using Wolverine;
using Wolverine.Configuration;
using Wolverine.Runtime;
using Wolverine.Runtime.Handlers;

namespace DocumentationSamples;

#region sample_WriteFile

// This has to be public btw
public record WriteFile(string Path, string Contents)
{
    public Task WriteAsync()
    {
        return File.WriteAllTextAsync(Path, Contents);
    }
}

#endregion

#region sample_WriteFilePolicy

internal class WriteFilePolicy : IChainPolicy
{
    // IChain is a Wolverine model to configure the code generation of
    // a message or HTTP handler and the core model for the application
    // of middleware
    public void Apply(IReadOnlyList<IChain> chains, GenerationRules rules, IServiceContainer container)
    {
        var method = ReflectionHelper.GetMethod<WriteFile>(x => x.WriteAsync());

        // Check out every message and/or http handler:
        foreach (var chain in chains)
        {
            var writeFiles = chain.ReturnVariablesOfType<WriteFile>();
            foreach (var writeFile in writeFiles)
            {
                // This is telling Wolverine to handle any return value
                // of WriteFile by calling its WriteAsync() method
                writeFile.UseReturnAction(_ =>
                {
                    // This is important, return a separate MethodCall
                    // object for each individual WriteFile variable
                    return new MethodCall(typeof(WriteFile), method!)
                    {
                        Target = writeFile
                    };
                });
            }
        }
    }
}

#endregion

public static class configure_return_values
{
    public static async Task configure()
    {
        #region sample_register_WriteFilePolicy

        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts => { opts.Policies.Add<WriteFilePolicy>(); }).StartAsync();

        #endregion
    }
}