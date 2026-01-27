using JasperFx;
using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Model;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Module1;
using Wolverine.ComplianceTests;
using Wolverine.ComplianceTests.Compliance;
using Wolverine.Configuration;
using Wolverine.Runtime;
using Wolverine.Runtime.Handlers;
using Wolverine.Runtime.Scheduled;
using Wolverine.Tracking;
using Xunit;

namespace CoreTests.Configuration;

public class bootstrapping_specs : IntegrationContext
{
    public bootstrapping_specs(DefaultApp @default) : base(@default)
    {
    }

    [Fact]
    public void registers_the_supplemental_code_files()
    {
        with(_ => {});

        var container = Host.Services.GetRequiredService<IServiceContainer>();
        container.DefaultFor<WolverineSupplementalCodeFiles>()
            .Lifetime.ShouldBe(ServiceLifetime.Singleton);

        container.GetAllInstances<ICodeFileCollection>()
            .OfType<WolverineSupplementalCodeFiles>()
            .Any()
            .ShouldBeTrue();
    }

    [Fact]
    public void can_apply_a_wrapper_to_all_chains()
    {
        with(opts => opts.Policies.Add<WrapWithSimple>());

        chainFor<MovieAdded>().Middleware.OfType<SimpleWrapper>().Any().ShouldBeTrue();
    }

    [Fact]
    public void can_customize_source_code_generation()
    {
        with(opts =>
        {
            opts.CodeGeneration.Sources.Add(new SpecialServiceSource());
            opts.IncludeType<SpecialServiceUsingThing>();
        });


        chainFor<Message1>()
            .ShouldHaveHandler<SpecialServiceUsingThing>(x => x.Handle(null, null));
    }

    [Fact]
    public async Task bootstrap_with_extension_finding_disabled()
    {
        #region sample_disabling_assembly_scanning

        using var host = await Microsoft.Extensions.Hosting.Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.DisableConventionalDiscovery();
            }, ExtensionDiscovery.ManualOnly)
            
            .StartAsync();

        #endregion

        var container = host.Services.GetRequiredService<IServiceContainer>();
        
        // IModuleService would be registered by the Module1Extension
        container.HasRegistrationFor<IModuleService>().ShouldBeFalse();
    }

    [Fact]
    public void application_service_registrations_win()
    {
        using var host = Microsoft.Extensions.Hosting.Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.DisableConventionalDiscovery();

                opts.Services.AddScoped<IModuleService, AppsModuleService>();
            }).Start();

        host.Services.GetRequiredService<IServiceContainer>().DefaultFor<IModuleService>().ImplementationType.ShouldBe(typeof(AppsModuleService));
    }

    [Fact]
    public void handler_classes_are_scoped()
    {
        Host.Get<IServiceContainer>().DefaultFor<SomeHandler>()
            .Lifetime.ShouldBe(ServiceLifetime.Scoped);
    }

    [Fact]
    public void with_aspnet_core()
    {
        var options = Host.Get<IOptions<LoggerFilterOptions>>();
        var logging = options.Value;


        var logger = Host.Get<ILogger<Thing>>();


        logger.ShouldNotBeNull();
    }

    [Theory]
    [InlineData(typeof(IMessageBus))]
    [InlineData(typeof(IMessageContext))]
    [InlineData(typeof(IMessageBus))]
    public void can_build_services(Type serviceType)
    {
        using var scope = Host.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService(serviceType)
            .ShouldNotBeNull();
    }

    [Fact]
    public void handler_graph_already_has_the_scheduled_send_handler()
    {
        var handlers = Host.GetRuntime().Handlers;

        handlers.HandlerFor<Envelope>().ShouldBeOfType<ScheduledSendEnvelopeHandler>();
    }

    public class ThingThatUsesContext
    {
        public ThingThatUsesContext(IMessageContext context)
        {
            Context = context;
        }

        public IMessageContext Context { get; }
    }

    public class AppsModuleService : IModuleService;

    public class SomeMessage;

    public class SomeHandler
    {
        public void Handle(SomeMessage message)
        {
        }
    }
}

public class Thing
{
    public Thing(ILogger<Thing> logger)
    {
        Logger = logger;
    }

    public ILogger<Thing> Logger { get; }
}

public class SimpleWrapper : Frame
{
    public SimpleWrapper() : base(false)
    {
    }

    public override void GenerateCode(GeneratedMethod method, ISourceWriter writer)
    {
        writer.Write("// Just a comment that SimpleWrapper was there");

        Next?.GenerateCode(method, writer);
    }
}

public class SpecialServiceUsingThing
{
    public void Handle(Message1 message, SpecialService service)
    {
    }
}

public class SpecialServiceSource : StaticVariable
{
    public SpecialServiceSource() : base(typeof(SpecialService),
        $"{typeof(SpecialService).FullName}.{nameof(SpecialService.Instance)}")
    {
    }
}

public class SpecialService
{
    public static readonly SpecialService Instance = new();

    private SpecialService()
    {
    }
}