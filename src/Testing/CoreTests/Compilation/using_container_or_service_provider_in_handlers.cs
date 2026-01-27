using JasperFx.CodeGeneration;
using JasperFx.Core;
using Microsoft.Extensions.DependencyInjection;
using Wolverine.Attributes;
using Wolverine.ComplianceTests;
using Xunit;
using Xunit.Abstractions;

namespace CoreTests.Compilation;

public class using_container_or_service_provider_in_handlers : CompilationContext
{
    private readonly ITestOutputHelper _output;

    public using_container_or_service_provider_in_handlers(ITestOutputHelper output)
    {
        _output = output;

        IfWolverineIsConfiguredAs(opts =>
        {
            opts.CodeGeneration.TypeLoadMode = TypeLoadMode.Auto;
            opts.IncludeType<CSP3Handler>();
            opts.IncludeType<CSP4Handler>();
        });
    }

    [Fact]
    public async Task IServiceProvider_as_constructor_dependency()
    {
        await Execute(new CSP3());
    }

    [Fact]
    public async Task IServiceProvider_as_method_parameter()
    {
        await Execute(new CSP4());
    }

    [Fact]
    public async Task using_service_location_with_one_service()
    {
        IfWolverineIsConfiguredAs(opts =>
        {
            opts.IncludeType(typeof(CSP5User));
            opts.CodeGeneration.AlwaysUseServiceLocationFor<IFlag>();

            opts.Services.AddScoped<IGateway, Gateway>();
            opts.Services.AddScoped<IFlag>(x =>
            {
                var context = x.GetRequiredService<ColorContext>();
                return context.Color.EqualsIgnoreCase("red") ? new RedFlag() : new GreenFlag();
            });

            opts.Services.AddSingleton(new ColorContext("Red"));
        });
        
        await Execute(new CSP5());

        CSP5User.Flag.ShouldBeOfType<RedFlag>();
    }
}

public class CSP3;

public class CSP3Handler
{
    private readonly IServiceProvider _container;

    public CSP3Handler(IServiceProvider container)
    {
        _container = container;
    }

    public void Handle(CSP3 message)
    {
        _container.ShouldNotBeNull();
    }
}

public class CSP4;

public class CSP4Handler
{
    public void Handle(CSP4 message, IServiceProvider container)
    {
        container.ShouldNotBeNull();
    }
}

public record CSP5;

// Just need this to be explicit
[WolverineIgnore]
public class CSP5User
{
    private readonly IFlag _flag;
    private readonly IGateway _gateway;
    public static IFlag? Flag { get; set; }

    public CSP5User(IFlag flag, IGateway gateway)
    {
        _flag = flag;
        _gateway = gateway;
    }

    public void Handle(CSP5 message)
    {
        Flag = _flag;
        _gateway.ShouldNotBeNull();
    }
}

public interface IFlag;
public record ColorContext(string Color);

public record RedFlag : IFlag;
public record GreenFlag : IFlag;

public interface IGateway;
public class Gateway : IGateway;