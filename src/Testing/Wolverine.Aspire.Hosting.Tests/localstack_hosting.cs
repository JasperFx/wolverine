using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Shouldly;
using Xunit;

namespace Wolverine.Aspire.Hosting.Tests;

/// <summary>
/// GH-5604: WolverineFx.Aspire.Hosting.LocalStack
/// </summary>
public class localstack_hosting
{
    [Fact]
    public void adds_a_localstack_container_running_sqs_and_sns()
    {
        var builder = DistributedApplication.CreateBuilder();
        var localStack = builder.AddLocalStack("aws").Resource;

        localStack.TryGetLastAnnotation<ContainerImageAnnotation>(out var image).ShouldBeTrue();
        image!.Image.ShouldBe(LocalStackBuilderExtensions.Image);
        image.Tag.ShouldBe(LocalStackBuilderExtensions.Tag);

        var endpoint = localStack.Annotations.OfType<EndpointAnnotation>().ShouldHaveSingleItem();
        endpoint.Name.ShouldBe("http");
        endpoint.TargetPort.ShouldBe(4566);

        localStack.Region.ShouldBe("us-east-1");

        // WaitFor() gates a project on this
        localStack.Annotations.OfType<HealthCheckAnnotation>().ShouldHaveSingleItem();
    }

    [Fact]
    public async Task the_container_only_starts_sqs_and_sns()
    {
        var builder = DistributedApplication.CreateBuilder();
        var localStack = builder.AddLocalStack("aws").Resource;

        var environment = await localStack.GetEnvironmentVariableValuesAsync(DistributedApplicationOperation.Publish);
        environment["SERVICES"].ShouldBe("sqs,sns");
    }

    [Fact]
    public void honors_a_fixed_host_port_and_region()
    {
        var builder = DistributedApplication.CreateBuilder();
        var localStack = builder.AddLocalStack("aws", port: 14566, region: "eu-west-1").Resource;

        localStack.Annotations.OfType<EndpointAnnotation>().Single().Port.ShouldBe(14566);
        localStack.Region.ShouldBe("eu-west-1");
    }

    [Fact]
    public async Task a_referencing_project_gets_the_endpoint_and_the_aws_sdk_environment()
    {
        var builder = DistributedApplication.CreateBuilder();
        var localStack = builder.AddLocalStack("localstack", region: "eu-west-1");

        var environment = await builder.EnvironmentOfAReferencingProjectAsync(x => x.WithReference(localStack));

        environment["ConnectionStrings__localstack"].ShouldBe("{localstack.connectionString}");
        localStack.Resource.ConnectionStringExpression.ValueExpression.ShouldBe("{localstack.bindings.http.url}");

        // What the AWS SDK reads for any client the project builds itself
        environment["AWS_ENDPOINT_URL"].ShouldBe("{localstack.bindings.http.url}");
        environment["AWS_REGION"].ShouldBe("eu-west-1");
        environment["AWS_DEFAULT_REGION"].ShouldBe("eu-west-1");
        environment["AWS_ACCESS_KEY_ID"].ShouldBe("test");
        environment["AWS_SECRET_ACCESS_KEY"].ShouldBe("test");

        environment["LOCALSTACK_REGION"].ShouldBe("eu-west-1");
    }

    [Fact]
    public async Task the_sample_apphost_runs_wolverine_against_sqs_and_sns()
    {
        var cancellation = TestContext.Current.CancellationToken;

        await using var app = await SampleAppHost.StartAsync<Projects.AspireWithLocalStack_AppHost>("aws", cancellation);
        await app.ShouldRoundTripAPingAsync(cancellation);
    }
}
