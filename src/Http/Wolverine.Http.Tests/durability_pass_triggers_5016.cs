using Alba;
using JasperFx.Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine.Persistence;
using Wolverine.Persistence.Durability;

namespace Wolverine.Http.Tests;

public class durability_pass_triggers_5016
{
    private static async Task<IAlbaHost> hostWith(params IDurabilityPassStep[] steps)
    {
        var builder = WebApplication.CreateBuilder([]);
        builder.Host.UseWolverine(opts =>
        {
            foreach (var step in steps)
            {
                opts.Services.AddSingleton(step);
            }
        });

        builder.Services.DisableAllExternalWolverineTransports();
        builder.Services.AddWolverineHttp();

        return await AlbaHost.For(builder, app =>
        {
            #region sample_map_wolverine_durability_endpoints

            // Protect this endpoint before you deploy it, for example with
            // .RequireAuthorization() on the returned route group
            app.MapWolverineDurabilityEndpoints();

            #endregion
        });
    }

    [Fact]
    public async Task post_runs_one_durability_pass_and_reports_it()
    {
        var step = new CountingStep("catch-up", processed: 3);
        await using var host = await hostWith(step);

        var result = await host.Scenario(x =>
        {
            x.Post.Url("/_wolverine/durability/pass");
            x.StatusCodeShouldBeOk();
        });

        step.Calls.ShouldBe(1);

        var response = await result.ReadAsJsonAsync<DurabilityPassResponse>();
        response.ShouldNotBeNull();
        response.OtherProcessed.ShouldBe(3);
        response.FailedSteps.ShouldBe(0);
        response.HasMoreWork.ShouldBeFalse();

        var stepResponse = response.Stores.SelectMany(x => x.Steps).Single(x => x.StepName == "catch-up");
        stepResponse.Status.ShouldBe(nameof(DurabilityPassStepStatus.Completed));
        stepResponse.Processed.ShouldBe(3);
        stepResponse.Calls.ShouldBe(1);
    }

    [Fact]
    public async Task query_string_bounds_the_pass()
    {
        var step = new CountingStep("catch-up", processed: 1);
        await using var host = await hostWith(step);

        await host.Scenario(x =>
        {
            x.Post.Url("/_wolverine/durability/pass?maxDuration=10&batchSize=7");
            x.StatusCodeShouldBeOk();
        });

        step.LastBatchSize.ShouldBe(7);
        step.LastTimeRemaining.ShouldBeGreaterThan(5.Seconds());
        step.LastTimeRemaining.ShouldBeLessThanOrEqualTo(10.Seconds());
    }

    [Fact]
    public async Task a_failing_step_answers_500_with_the_report()
    {
        await using var host = await hostWith(new FailingStep(), new CountingStep("after", processed: 2));

        var result = await host.Scenario(x =>
        {
            x.Post.Url("/_wolverine/durability/pass");
            x.StatusCodeShouldBe(500);
        });

        var response = await result.ReadAsJsonAsync<DurabilityPassResponse>();
        response.ShouldNotBeNull();
        response.FailedSteps.ShouldBe(1);

        var steps = response.Stores.SelectMany(x => x.Steps).ToArray();
        var failed = steps.Single(x => x.StepName == "boom");
        failed.Status.ShouldBe(nameof(DurabilityPassStepStatus.Failed));
        failed.Error.ShouldBe("The step blew up");

        // The rest of the pass carries on
        steps.Single(x => x.StepName == "after").Processed.ShouldBe(2);
    }

    [Fact]
    public async Task can_map_under_a_custom_prefix()
    {
        var step = new CountingStep("catch-up", processed: 1);

        var builder = WebApplication.CreateBuilder([]);
        builder.Host.UseWolverine(opts => opts.Services.AddSingleton<IDurabilityPassStep>(step));
        builder.Services.DisableAllExternalWolverineTransports();
        builder.Services.AddWolverineHttp();

        await using var host = await AlbaHost.For(builder, app => app.MapWolverineDurabilityEndpoints("/ops/durability"));

        await host.Scenario(x =>
        {
            x.Post.Url("/ops/durability/pass");
            x.StatusCodeShouldBeOk();
        });

        step.Calls.ShouldBe(1);
    }

    [Fact]
    public void prefix_cannot_be_empty()
    {
        var builder = WebApplication.CreateBuilder([]);
        builder.Host.UseWolverine();
        var app = builder.Build();

        Should.Throw<ArgumentNullException>(() => app.MapWolverineDurabilityEndpoints(""));
    }

    private static IHostBuilder cliHost(params IDurabilityPassStep[] steps)
    {
        return Host.CreateDefaultBuilder().UseWolverine(opts =>
        {
            opts.Services.DisableAllExternalWolverineTransports();
            foreach (var step in steps)
            {
                opts.Services.AddSingleton(step);
            }
        });
    }

    [Fact]
    public async Task storage_flush_runs_one_durability_pass()
    {
        var step = new CountingStep("catch-up", processed: 4);

        var input = new StorageInput
        {
            HostBuilder = cliHost(step),
            Action = StorageCommandAction.flush,
            MaxDurationFlag = 10,
            BatchSizeFlag = 5
        };

        var succeeded = await new StorageCommand().Execute(input);

        succeeded.ShouldBeTrue();
        step.Calls.ShouldBe(1);
        step.LastBatchSize.ShouldBe(5);
        step.LastTimeRemaining.ShouldBeLessThanOrEqualTo(10.Seconds());
    }

    [Fact]
    public async Task storage_flush_fails_when_a_step_fails()
    {
        var after = new CountingStep("after", processed: 1);

        var input = new StorageInput
        {
            HostBuilder = cliHost(new FailingStep(), after),
            Action = StorageCommandAction.flush
        };

        var succeeded = await new StorageCommand().Execute(input);

        succeeded.ShouldBeFalse();
        after.Calls.ShouldBe(1);
    }
}

public class CountingStep : IDurabilityPassStep
{
    private readonly int _processed;

    public CountingStep(string name, int processed)
    {
        Name = name;
        _processed = processed;
    }

    public string Name { get; }
    public int Calls { get; private set; }
    public int LastBatchSize { get; private set; }
    public TimeSpan LastTimeRemaining { get; private set; }

    public Task<DurabilityPassStepResult> ExecuteAsync(DurabilityPassContext context, CancellationToken token)
    {
        Calls++;
        LastBatchSize = context.BatchSize;
        LastTimeRemaining = context.TimeRemaining;
        return Task.FromResult(new DurabilityPassStepResult { OtherProcessed = _processed });
    }
}

public class FailingStep : IDurabilityPassStep
{
    public string Name => "boom";

    public Task<DurabilityPassStepResult> ExecuteAsync(DurabilityPassContext context, CancellationToken token)
    {
        throw new InvalidOperationException("The step blew up");
    }
}
