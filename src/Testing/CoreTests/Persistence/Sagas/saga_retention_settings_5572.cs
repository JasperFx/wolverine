using JasperFx.Core;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine.Persistence.Sagas;
using Wolverine.Tracking;
using Xunit;

namespace CoreTests.Persistence.Sagas;

public record StartInMemoryRetainedSaga(Guid Id);

public record FinishInMemoryRetainedSaga(Guid Id);

public record TouchInMemoryRetainedSaga(Guid Id);

[RetainWhenCompleted(Days = 1, Hours = 12)]
public class InMemoryRetainedSaga : Saga
{
    public Guid Id { get; set; }

    public static InMemoryRetainedSaga Start(StartInMemoryRetainedSaga command) => new() { Id = command.Id };

    public void Handle(FinishInMemoryRetainedSaga command) => MarkCompleted();

    public void Handle(TouchInMemoryRetainedSaga command)
    {
    }
}

public class SagaWithoutRetention : Saga
{
    public Guid Id { get; set; }
}

[RetainWhenCompleted]
public class SagaWithEmptyRetention : Saga
{
    public Guid Id { get; set; }
}

// GH-5572
public class saga_retention_settings_5572
{
    [Fact]
    public void retention_is_off_by_default()
    {
        var options = new WolverineOptions();
        options.Durability.CompletedSagaRetention.ShouldBeNull();
        SagaRetention.For(typeof(SagaWithoutRetention), options).ShouldBeNull();
    }

    [Fact]
    public void the_policy_sets_the_retention_for_every_saga_type()
    {
        var options = new WolverineOptions();
        options.Policies.RetainCompletedSagas(3.Days());

        options.Durability.CompletedSagaRetention.ShouldBe(3.Days());
        SagaRetention.For(typeof(SagaWithoutRetention), options).ShouldBe(3.Days());
    }

    [Fact]
    public void the_attribute_wins_over_the_policy()
    {
        var options = new WolverineOptions();
        options.Policies.RetainCompletedSagas(3.Days());

        SagaRetention.For(typeof(InMemoryRetainedSaga), options).ShouldBe(36.Hours());
    }

    [Fact]
    public void the_retention_period_must_be_positive()
    {
        Should.Throw<ArgumentOutOfRangeException>(() => new WolverineOptions().Policies.RetainCompletedSagas(TimeSpan.Zero));
        Should.Throw<InvalidSagaException>(() => SagaRetention.ExplicitFor(typeof(SagaWithEmptyRetention)));
    }

    [Fact]
    public void an_open_saga_is_not_completed_and_a_completed_at_marks_it_completed()
    {
        var saga = new SagaWithoutRetention();
        saga.IsCompleted().ShouldBeFalse();

        SagaRetention.MarkCompleted(saga);
        saga.CompletedAt.ShouldNotBeNull();
        saga.IsCompleted().ShouldBeTrue();

        // Marking it again keeps the first completion time
        var first = saga.CompletedAt;
        SagaRetention.MarkCompleted(saga);
        saga.CompletedAt.ShouldBe(first);
    }

    [Fact]
    public async Task sagas_kept_in_memory_are_deleted_as_before()
    {
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts => opts.Discovery.DisableConventionalDiscovery().IncludeType<InMemoryRetainedSaga>())
            .StartAsync(TestContext.Current.CancellationToken);

        var id = Guid.NewGuid();
        await host.InvokeMessageAndWaitAsync(new StartInMemoryRetainedSaga(id));
        await host.InvokeMessageAndWaitAsync(new FinishInMemoryRetainedSaga(id));

        host.GetRuntime().Handlers.ChainFor<FinishInMemoryRetainedSaga>().ShouldBeOfType<SagaChain>()
            .CompletedSagaRetention.ShouldBeNull();

        await Should.ThrowAsync<UnknownSagaException>(() =>
            host.MessageBus().InvokeAsync(new TouchInMemoryRetainedSaga(id), TestContext.Current.CancellationToken));
    }
}
