using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Shouldly;
using Xunit;

namespace Wolverine.Aspire.Hosting.Tests;

/// <summary>
/// GH-5605: WolverineFx.Aspire.Hosting.Pubsub
/// </summary>
public class pubsub_hosting
{
    [Fact]
    public void adds_the_gcloud_pubsub_emulator_container()
    {
        var builder = DistributedApplication.CreateBuilder();
        var emulator = builder.AddPubsubEmulator("pubsub").Resource;

        emulator.TryGetLastAnnotation<ContainerImageAnnotation>(out var image).ShouldBeTrue();
        image!.Registry.ShouldBe(PubsubEmulatorBuilderExtensions.Registry);
        image.Image.ShouldBe(PubsubEmulatorBuilderExtensions.Image);
        image.Tag.ShouldBe(PubsubEmulatorBuilderExtensions.Tag);

        var endpoint = emulator.Annotations.OfType<EndpointAnnotation>().ShouldHaveSingleItem();
        endpoint.TargetPort.ShouldBe(8085);

        emulator.ProjectId.ShouldBe("wolverine");

        // WaitFor() gates a project on this
        emulator.Annotations.OfType<HealthCheckAnnotation>().ShouldHaveSingleItem();
    }

    [Fact]
    public async Task starts_the_emulator_for_the_project_id()
    {
        var builder = DistributedApplication.CreateBuilder();
        var emulator = builder.AddPubsubEmulator("pubsub", projectId: "orders-local").Resource;

        var args = await emulator.GetArgumentValuesAsync(DistributedApplicationOperation.Publish);
        args.ShouldBe(["gcloud", "--quiet", "beta", "emulators", "pubsub", "start", "--host-port=0.0.0.0:8085",
            "--project=orders-local"]);
    }

    [Fact]
    public void honors_a_fixed_host_port()
    {
        var builder = DistributedApplication.CreateBuilder();
        var emulator = builder.AddPubsubEmulator("pubsub", port: 18085).Resource;

        emulator.Annotations.OfType<EndpointAnnotation>().Single().Port.ShouldBe(18085);
    }

    [Fact]
    public async Task a_referencing_project_gets_the_endpoint_and_the_emulator_environment()
    {
        var builder = DistributedApplication.CreateBuilder();
        var emulator = builder.AddPubsubEmulator("gcp", projectId: "orders-local");

        var environment = await builder.EnvironmentOfAReferencingProjectAsync(x => x.WithReference(emulator));

        environment["ConnectionStrings__gcp"].ShouldBe("{gcp.connectionString}");
        emulator.Resource.ConnectionStringExpression.ValueExpression
            .ShouldBe("{gcp.bindings.http.host}:{gcp.bindings.http.port}");

        // What `gcloud beta emulators pubsub env-init` sets
        environment["PUBSUB_EMULATOR_HOST"].ShouldBe("{gcp.bindings.http.host}:{gcp.bindings.http.port}");
        environment["PUBSUB_PROJECT_ID"].ShouldBe("orders-local");

        environment["GCP_PROJECTID"].ShouldBe("orders-local");
    }

    [Fact]
    public async Task the_sample_apphost_runs_wolverine_against_the_emulator()
    {
        var cancellation = TestContext.Current.CancellationToken;

        await using var app = await SampleAppHost.StartAsync<Projects.AspireWithPubsub_AppHost>("pubsub", cancellation);
        await app.ShouldRoundTripAPingAsync(cancellation);
    }
}
