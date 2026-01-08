using JasperFx;
using JasperFx.CodeGeneration.Frames;
using JasperFx.MultiTenancy;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace Wolverine.Http.Tests.MultiTenancy;

public class using_form_data_on_primitives_with_tenant_id
{
    [Fact]
    public void  determine_that_it_is_form_data_and_tenant_id_is_sourced_from_tenant_detection()
    {
        var method = new MethodCall(typeof(TenantedEndpoints), "GetTenantIdWithFormData");
        var serviceCollection = new ServiceCollection();
        var chain1 = new HttpChain(method,
            new HttpGraph(new WolverineOptions(), new ServiceContainer(serviceCollection, serviceCollection.BuildServiceProvider())));

        chain1.IsFormData.ShouldBeTrue();
        chain1.RequestType.ShouldNotBe(typeof(TenantId));

    }
}