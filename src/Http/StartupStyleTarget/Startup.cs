using Wolverine;
using Wolverine.Http;

namespace StartupStyleTarget;

public class Startup
{
    public void ConfigureServices(IServiceCollection services)
    {
        services.AddWolverineHttp();
    }

    public void Configure(IApplicationBuilder app, IWebHostEnvironment env)
    {
        app.UseRouting();

        app.UseEndpoints(endpoints =>
        {
            endpoints.MapWolverineEndpoints();
        });
    }
}
