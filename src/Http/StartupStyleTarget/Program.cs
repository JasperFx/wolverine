using JasperFx;
using Wolverine;

namespace StartupStyleTarget;

public class Program
{
    public static Task<int> Main(string[] args)
    {
        return Host.CreateDefaultBuilder(args)
            .UseWolverine()
            .ConfigureWebHostDefaults(webBuilder =>
            {
                webBuilder.UseStartup<Startup>();
            })
            .RunJasperFxCommands(args);
    }
}
