using JasperFx;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Wolverine.ComplianceTests.Compliance;
using Wolverine.Runtime;
using Wolverine.Runtime.Handlers;
using Xunit;

namespace CoreTests.Runtime.Handlers;

public class finding_service_dependencies_of_a_chain
{
    private readonly HandlerChain
        theChain = HandlerChain.For<FakeDudeWithAction>(x => x.Handle(null, null, null), null);


    private IServiceContainer theContainer;

    public finding_service_dependencies_of_a_chain()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IService1>(Substitute.For<IService1>());
        services.AddSingleton<IService2>(Substitute.For<IService2>());
        services.AddSingleton<IService3>(Substitute.For<IService3>());
        services.AddSingleton<IService4>(Substitute.For<IService4>());

        services.AddSingleton<IService5, Service5>();
        theContainer = new ServiceContainer(services, services.BuildServiceProvider());
    }

    [Fact]
    public void find_dependencies_in_parameter_list()
    {
        var dependencies = theChain.ServiceDependencies(theContainer, Type.EmptyTypes).ToArray();

        dependencies.ShouldContain(typeof(IService1));
        dependencies.ShouldContain(typeof(IService2));
    }

    [Fact]
    public void find_dependencies_of_ctor()
    {
        var dependencies = theChain.ServiceDependencies(theContainer, Type.EmptyTypes).ToArray();

        dependencies.ShouldContain(typeof(IService3));
        dependencies.ShouldContain(typeof(IService5));
    }

    [Fact]
    public void find_dependencies_deep()
    {
        var dependencies = theChain.ServiceDependencies(theContainer, Type.EmptyTypes).ToArray();

        dependencies.ShouldContain(typeof(IService5));
    }

    public class FakeDudeWithAction
    {
        public FakeDudeWithAction(IService3 three, IService5 five)
        {
        }

        public void Handle(Message1 message, IService1 one, IService2 two)
        {
        }
    }

    public interface IService1;
    public interface IService2;

    public interface IService3;

    public interface IService4;

    public interface IService5;

    public class Service5(IService5 five) : IService5;
}