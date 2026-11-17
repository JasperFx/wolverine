using Wolverine.Attributes;
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

public record StartSagaWithItsOwnCompletedAt(Guid Id);

// Declares its own CompletedAt, which hides Saga.CompletedAt. Kept out of conventional discovery, which would
// otherwise hand it to every CoreTests host that retains completed sagas
[WolverineIgnore]
public class SagaWithItsOwnCompletedAt : Saga
{
    public Guid Id { get; set; }

#pragma warning disable CS0108
    public DateTime? CompletedAt { get; set; }
#pragma warning restore CS0108

    public static SagaWithItsOwnCompletedAt Start(StartSagaWithItsOwnCompletedAt command) => new() { Id = command.Id };
}

public record StartRetainedSagaWithItsOwnCompletedAt(Guid Id);

// Refused at startup, so kept out of conventional discovery: every CoreTests host scanning this assembly would fail
[WolverineIgnore]
[RetainWhenCompleted(Days = 1)]
public class RetainedSagaWithItsOwnCompletedAt : Saga
{
    public Guid Id { get; set; }

#pragma warning disable CS0108
    public DateTime? CompletedAt { get; set; }
#pragma warning restore CS0108

    public static RetainedSagaWithItsOwnCompletedAt Start(StartRetainedSagaWithItsOwnCompletedAt command) =>
        new() { Id = command.Id };
}

// GH-5572
public class saga_retention_settings_5572
{
    [Fact]
    public async Task a_retained_saga_that_hides_completed_at_is_refused_at_startup()
    {
        var ex = await Should.ThrowAsync<InvalidSagaException>(async () =>
        {
            using var host = await Host.CreateDefaultBuilder()
                .UseWolverine(opts =>
                    opts.Discovery.DisableConventionalDiscovery().IncludeType<RetainedSagaWithItsOwnCompletedAt>())
                .StartAsync(TestContext.Current.CancellationToken);
        });

        ex.Message.ShouldContain("declares its own CompletedAt");
    }

    [Fact]
    public async Task the_retention_policy_also_refuses_a_saga_that_hides_completed_at()
    {
        await Should.ThrowAsync<InvalidSagaException>(async () =>
        {
            using var host = await Host.CreateDefaultBuilder()
                .UseWolverine(opts =>
                {
                    opts.Policies.RetainCompletedSagas(1.Days());
                    opts.Discovery.DisableConventionalDiscovery().IncludeType<SagaWithItsOwnCompletedAt>();
                })
                .StartAsync(TestContext.Current.CancellationToken);
        });
    }

    [Fact]
    public async Task a_saga_that_hides_completed_at_is_fine_with_retention_off()
    {
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts => opts.Discovery.DisableConventionalDiscovery().IncludeType<SagaWithItsOwnCompletedAt>())
            .StartAsync(TestContext.Current.CancellationToken);

        await host.InvokeMessageAndWaitAsync(new StartSagaWithItsOwnCompletedAt(Guid.NewGuid()));
    }

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
