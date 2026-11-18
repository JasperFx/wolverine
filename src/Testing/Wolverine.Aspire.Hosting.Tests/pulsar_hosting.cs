using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Shouldly;
using Xunit;

namespace Wolverine.Aspire.Hosting.Tests;

/// <summary>
/// GH-5603: WolverineFx.Aspire.Hosting.Pulsar
/// </summary>
public class pulsar_hosting
{
    [Fact]
    public void adds_a_standalone_pulsar_container()
    {
        var builder = DistributedApplication.CreateBuilder();
        var pulsar = builder.AddPulsar("pulsar").Resource;

        pulsar.TryGetLastAnnotation<ContainerImageAnnotation>(out var image).ShouldBeTrue();
        image!.Image.ShouldBe(PulsarBuilderExtensions.Image);
        image.Tag.ShouldBe(PulsarBuilderExtensions.Tag);

        var endpoints = pulsar.Annotations.OfType<EndpointAnnotation>().ToDictionary(x => x.Name);
        endpoints["tcp"].TargetPort.ShouldBe(6650);
        endpoints["http"].TargetPort.ShouldBe(8080);

        // WaitFor() gates a project on this
        pulsar.Annotations.OfType<HealthCheckAnnotation>().ShouldHaveSingleItem();
    }

    [Fact]
    public void honors_fixed_host_ports()
    {
        var builder = DistributedApplication.CreateBuilder();
        var pulsar = builder.AddPulsar("pulsar", port: 16650, adminPort: 18080).Resource;

        var endpoints = pulsar.Annotations.OfType<EndpointAnnotation>().ToDictionary(x => x.Name);
        endpoints["tcp"].Port.ShouldBe(16650);
        endpoints["http"].Port.ShouldBe(18080);
    }

    [Fact]
    public async Task a_referencing_project_gets_the_service_url_and_the_admin_url()
    {
        var builder = DistributedApplication.CreateBuilder();
        var pulsar = builder.AddPulsar("pulsar");

        var environment = await builder.EnvironmentOfAReferencingProjectAsync(x => x.WithReference(pulsar));

        environment["ConnectionStrings__pulsar"].ShouldBe("{pulsar.connectionString}");
        pulsar.Resource.ConnectionStringExpression.ValueExpression
            .ShouldBe("pulsar://{pulsar.bindings.tcp.host}:{pulsar.bindings.tcp.port}");

        environment["PULSAR_HOST"].ShouldBe("{pulsar.bindings.tcp.host}");
        environment["PULSAR_PORT"].ShouldBe("{pulsar.bindings.tcp.port}");
        environment["PULSAR_ADMINURL"].ShouldBe("{pulsar.bindings.http.url}");
    }

    [Fact]
    public async Task the_sample_apphost_runs_wolverine_against_the_broker()
    {
        var cancellation = TestContext.Current.CancellationToken;

        await using var app = await SampleAppHost.StartAsync<Projects.AspireWithPulsar_AppHost>("pulsar", cancellation);
        await app.ShouldRoundTripAPingAsync(cancellation);
    }
}
