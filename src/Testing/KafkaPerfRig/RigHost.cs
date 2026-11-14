using JasperFx;
using JasperFx.CodeGeneration;
using Microsoft.Extensions.Hosting;

namespace KafkaPerfRig;

/// <summary>
/// GH-5544. Wolverine 7.0 (GH-5523) loads only pre-generated handler code in the Production profile and
/// refuses to compile it at runtime. The rig never pre-generates code, so every lane starts its host from
/// here, which asks for runtime compilation explicitly.
/// </summary>
public static class RigHost
{
    public static IHostBuilder CreateDefaultBuilder()
    {
        return Host.CreateDefaultBuilder()
            .ConfigureServices(services =>
                services.CritterStackDefaults(x => x.Production.GeneratedCodeMode = TypeLoadMode.Dynamic));
    }
}
