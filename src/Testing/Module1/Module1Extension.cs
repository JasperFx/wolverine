using Microsoft.Extensions.DependencyInjection;
using Wolverine;

namespace Module1;

public class Module1Extension : IWolverineExtension
{
    public static WolverineOptions Options { get; set; } = null!;

    public void Configure(WolverineOptions options)
    {
        Options = options;

        options.Services.AddScoped<IModuleService, ServiceFromModule>();
    }
}

public interface IModuleService;

public class ServiceFromModule : IModuleService;