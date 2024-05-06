using System;
using System.Linq;
using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Model;
using Lamar;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Module1;
using TestingSupport;
using TestingSupport.Compliance;
using Wolverine.Configuration;
using Wolverine.Runtime.Handlers;
using Wolverine.Runtime.Scheduled;
using Xunit;

namespace CoreTests.Configuration;

public class BootstrappingTests : IntegrationContext
{
    public BootstrappingTests(DefaultApp @default) : base(@default)
    {
    }

    [Fact]
    public void registers_the_supplemental_code_files()
    {
        with(_ => {});

        var container = (IContainer)Host.Services;
        container.Model.For<WolverineSupplementalCodeFiles>()
            .Default.Lifetime.ShouldBe(ServiceLifetime.Singleton);
        
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
    public void can_build_i_message_context()
    {
        Host.Get<IMessageContext>().ShouldNotBeNull();

        Host.Get<ThingThatUsesContext>()
            .Context.ShouldNotBeNull();
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
    public void application_service_registrations_win()
    {
        using var host = Microsoft.Extensions.Hosting.Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.DisableConventionalDiscovery();

                opts.Services.For<IModuleService>().Use<AppsModuleService>();
            }).Start();

        host.Services.GetRequiredService<IContainer>().DefaultRegistrationIs<IModuleService, AppsModuleService>();
    }

    [Fact]
    public void handler_classes_are_scoped()
    {
        // forcing the container to resolve the family
        var endpoint = Host.Get<SomeHandler>();

        Host.Get<IContainer>().Model.For<SomeHandler>().Default
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
        Host.Get(serviceType)
            .ShouldNotBeNull();
    }
    

    [Fact]
    public void handler_graph_already_has_the_scheduled_send_handler()
    {
        var handlers = Host.Get<HandlerGraph>();

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