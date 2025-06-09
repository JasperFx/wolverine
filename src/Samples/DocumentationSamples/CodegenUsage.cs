using JasperFx;
using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Commands;
using Microsoft.Extensions.Hosting;
using Wolverine;

namespace DocumentationSamples;

public class CodegenUsage
{
    public async Task override_codegen()
    {
        #region sample_codegen_type_load_mode

        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                // The default behavior. Dynamically generate the
                // types on the first usage
                opts.CodeGeneration.TypeLoadMode = TypeLoadMode.Dynamic;

                // Never generate types at runtime, but instead try to locate
                // the generated types from the main application assembly
                opts.CodeGeneration.TypeLoadMode = TypeLoadMode.Static;

                // Hybrid approach that first tries to locate the types
                // from the application assembly, but falls back to
                // generating the code and dynamic type. Also writes the
                // generated source code file to disk
                opts.CodeGeneration.TypeLoadMode = TypeLoadMode.Auto;
            }).StartAsync();

        #endregion
    }

    public async Task use_environment_check_on_expected_prebuilt_types()
    {
        #region sample_asserting_all_pre_built_types_exist_upfront

        var builder = Host.CreateApplicationBuilder();
        builder.UseWolverine(opts =>
            {
                if (builder.Environment.IsProduction())
                {
                    opts.CodeGeneration.TypeLoadMode = TypeLoadMode.Static;

                    opts.Services.CritterStackDefaults(cr =>
                    {
                        // I'm only going to care about this in production
                        cr.Production.AssertAllPreGeneratedTypesExist = true;
                    });
                }
            });

        using var host = builder.Build();
        await host.StartAsync();

        #endregion
    }

    public async Task use_optimized_workflow()
    {
        #region sample_use_optimized_workflow

        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                // Use "Auto" type load mode at development time, but
                // "Static" any other time
                opts.Services.CritterStackDefaults(x =>
                {
                    x.Production.GeneratedCodeMode = TypeLoadMode.Static;
                    x.Production.ResourceAutoCreate = AutoCreate.None;

                    // Little draconian, but this might be helpful
                    x.Production.AssertAllPreGeneratedTypesExist = true;

                    // These are defaults, but showing for completeness
                    x.Development.GeneratedCodeMode = TypeLoadMode.Dynamic;
                    x.Development.ResourceAutoCreate = AutoCreate.CreateOrUpdate;
                });
            }).StartAsync();

        #endregion
    }
}