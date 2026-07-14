using CoreTests.Acceptance;
using CoreTests.Bugs;
using CoreTests.Shims;
using JasperFx.Core.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NSubstitute;
using JasperFx.Resources;
using Wolverine.ComplianceTests;
using Wolverine.ComplianceTests.Compliance;
using Wolverine.Runtime;
using Wolverine.Runtime.Handlers;
using Wolverine.Tracking;
using Xunit;

namespace CoreTests;

public class DefaultApp : IDisposable
{
    public DefaultApp()
    {
        Host = Microsoft.Extensions.Hosting.Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Policies.MessageExecutionLogLevel(LogLevel.Information);
                opts.Policies.MessageSuccessLogLevel(LogLevel.Debug);
                
                opts.IncludeType<MessageConsumer>();
                opts.IncludeType<InvokedMessageHandler>();

                opts.Services.AddSingleton(Substitute.For<IIdentityService>());
                opts.Services.AddSingleton(Substitute.For<ITrackedTaskRepository>());
                
                opts.Services.AddScoped<IAdditionService, AdditionService>();
                
                opts.RegisterMessageType(typeof(ExplicitMessage1));
                opts.RegisterMessageType(typeof(ExplicitMessage2));
                opts.RegisterMessageType(typeof(ExplicitMessage3));
            })
            .UseResourceSetupOnStartup(StartupAction.ResetState).Start();
    }

    public IHost Host { get; private set; }

    public void Dispose()
    {
        Host.Dispose();
    }

    public HandlerChain ChainFor<T>()
    {
        return Host.Get<HandlerGraph>().HandlerFor<T>()!.As<MessageHandler>().Chain!;
    }
}

public class IntegrationContext : IDisposable, IClassFixture<DefaultApp>
{
    private readonly DefaultApp _default;

    // Only set when a test opts out of the shared fixture via with(). That host belongs to the
    // test and has to be disposed with it; the DefaultApp fixture does not, and must not be.
    private IHost? _ownedHost;

    public IntegrationContext(DefaultApp @default)
    {
        _default = @default;

        Host = _default.Host;
    }

    public IHost Host { get; private set; }

    public IMessageContext Publisher => new MessageContext(Host.GetRuntime());
    public IMessageBus Bus => Host.MessageBus();

    public HandlerGraph Handlers => Host.Get<HandlerGraph>();

    public virtual void Dispose()
    {
        // Dispose only what this test built for itself. DefaultApp is an IClassFixture, so xUnit
        // owns its lifetime and disposes it once the class finishes. Tearing it down here — after
        // every test method — left the *shared* host disposed for the tests that followed, and the
        // guard that papered over that silently rebuilt it as a WolverineHost.Basic() carrying none
        // of DefaultApp's handler/service registrations. Tests then passed or failed on ordering.
        // See GH-3423.
        _ownedHost?.Dispose();
    }

    protected async Task with(Action<WolverineOptions> configuration)
    {
        _ownedHost?.Dispose();

        _ownedHost = await WolverineHost.ForAsync(configuration);
        Host = _ownedHost;
    }

    protected HandlerChain chainFor<T>()
    {
        return Handlers.HandlerFor<T>()!.As<MessageHandler>().Chain!;
    }
}

public record ExplicitMessage1;
public record ExplicitMessage2;
public record ExplicitMessage3;