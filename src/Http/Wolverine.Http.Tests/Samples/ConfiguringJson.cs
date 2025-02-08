using Microsoft.AspNetCore.Builder;
using JasperFx;

namespace Wolverine.Http.Tests.Samples;

public class ConfiguringJson
{
    public static async Task<int> configure(params string[] args)
    {
        #region sample_configuring_stj_for_wolverine

        var builder = WebApplication.CreateBuilder();

        builder.Host.UseWolverine();

        builder.Services.ConfigureSystemTextJsonForWolverineOrMinimalApi(o =>
        {
            // Do whatever you want here to customize the JSON
            // serialization
            o.SerializerOptions.WriteIndented = true;
        });

        var app = builder.Build();

        app.MapWolverineEndpoints();

        return await app.RunJasperFxCommands(args);

        #endregion
    }
}