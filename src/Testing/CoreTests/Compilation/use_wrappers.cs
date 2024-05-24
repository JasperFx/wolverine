using Microsoft.Extensions.DependencyInjection;
using TestingSupport;
using TestingSupport.Compliance;
using TestingSupport.Fakes;
using Wolverine.Attributes;
using Xunit;

namespace CoreTests.Compilation;

public class use_wrappers : CompilationContext
{
    private readonly TestingSupport.Fakes.Tracking theTracking = new();

    public use_wrappers()
    {
        IfWolverineIsConfiguredAs(opts =>
        {
            opts.IncludeType<TransactionalHandler>();

            opts.Services.AddSingleton(theTracking);
            opts.Services.AddSingleton<IFakeStore, FakeStore>();
        });
    }

    [Fact]
    public async Task wrapper_applied_by_generic_attribute_executes()
    {
        var message = new Message2();

        await Execute(message);

        theTracking.DisposedTheSession.ShouldBeTrue();
        theTracking.OpenedSession.ShouldBeTrue();
        theTracking.CalledSaveChanges.ShouldBeTrue();
    }

    [Fact]
    public async Task wrapper_executes()
    {
        var message = new Message1();

        await Execute(message);

        theTracking.DisposedTheSession.ShouldBeTrue();
        theTracking.OpenedSession.ShouldBeTrue();
        theTracking.CalledSaveChanges.ShouldBeTrue();
    }
}

[WolverineIgnore]
public class TransactionalHandler
{
    [FakeTransaction]
    public void Handle(Message1 message)
    {
    }

    [GenericFakeTransaction]
    public void Handle(Message2 message)
    {
    }
}