using JasperFx.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Wolverine.Attributes;
using Wolverine.Persistence.Sagas;
using Wolverine.Tracking;
using Xunit;

namespace CoreTests.Persistence.Sagas;

// GH-5674: the rules around late messages and replacing timeouts, on in-memory sagas. The order approval and
// subscription stories are told by the SagaWorkflows specifications
public class saga_missing_and_replaced_timeouts_5674
{
    private static async Task<IHost> hostAsync(Action<WolverineOptions>? configure, params Type[] types)
    {
        return await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                var discovery = opts.Discovery.DisableConventionalDiscovery();
                foreach (var type in types) discovery.IncludeType(type);
                configure?.Invoke(opts);
            }).StartAsync(TestContext.Current.CancellationToken);
    }

    private static Task<ITrackedSession> sendAsync(IHost host, object message)
        => host.TrackActivity().DoNotAssertOnExceptionsDetected().Timeout(10.Seconds()).SendMessageAndWaitAsync(message);

    private static bool failedWithUnknownSaga(ITrackedSession session)
        => session.AllExceptions().OfType<UnknownSagaException>().Any();

    [Fact]
    public async Task the_default_is_unchanged()
    {
        using var host = await hostAsync(null, typeof(KennelSaga));

        var session = await sendAsync(host, new WalkDog(Guid.NewGuid()));

        failedWithUnknownSaga(session).ShouldBeTrue();
    }

    [Fact]
    public async Task the_global_policy_discards()
    {
        using var host = await hostAsync(opts => opts.Policies.WhenSagaIsMissing(SagaMissingBehavior.Discard),
            typeof(KennelSaga));

        var session = await sendAsync(host, new WalkDog(Guid.NewGuid()));

        failedWithUnknownSaga(session).ShouldBeFalse();
        session.Executed.SingleMessage<WalkDog>().ShouldNotBeNull();
    }

    [Fact]
    public async Task the_saga_level_attribute_wins_over_the_global_policy()
    {
        using var host = await hostAsync(opts => opts.Policies.WhenSagaIsMissing(SagaMissingBehavior.Discard),
            typeof(StrictKennelSaga));

        var session = await sendAsync(host, new FeedDog(Guid.NewGuid()));

        failedWithUnknownSaga(session).ShouldBeTrue();
    }

    [Fact]
    public async Task the_message_attribute_wins_over_the_saga_level_attribute()
    {
        using var host = await hostAsync(null, typeof(StrictKennelSaga));

        var session = await sendAsync(host, new GroomDog(Guid.NewGuid()));

        failedWithUnknownSaga(session).ShouldBeFalse();
    }

    [Fact]
    public async Task a_compensation_chain_still_reports_a_missing_saga()
    {
        using var host = await hostAsync(opts => opts.Policies.WhenSagaIsMissing(SagaMissingBehavior.Discard),
            typeof(KennelSaga));

        var chain = host.GetRuntime().Options.HandlerGraph.SagaChains()
            .Single(x => x.MessageType == typeof(Fault<WalkDog>));
        chain.DetermineSagaMissingBehavior(host.Services.GetRequiredService<WolverineOptions>())
            .ShouldBe(SagaMissingBehavior.Throw);
    }

    [Fact]
    public async Task a_timeout_with_its_own_key_replaces_the_earlier_one()
    {
        using var host = await hostAsync(null, typeof(KennelSaga));
        var id = Guid.NewGuid();

        var session = await host.TrackActivity().Timeout(10.Seconds()).ExecuteAndWaitAsync((Func<IMessageContext, Task>)(async context =>
        {
            await context.InvokeAsync(new OpenKennel(id));
            await context.InvokeAsync(new MoveFeeding(id, 2));
        }));

        var played = await session.PlayScheduledMessagesAsync(10.Seconds());

        // Two FeedingTime timeouts ran; only the one scheduled by MoveFeeding reached Handle
        played.Executed.MessagesOf<FeedingTime>().Count().ShouldBe(2);
        var saga = host.Services.GetRequiredService<InMemorySagaPersistor>().Load<KennelSaga>(id)!;
        saga.Feedings.ShouldBe([2]);
        saga.LatestTimeoutIds!.Keys.ShouldBe(["feeding"]);
    }

    [Fact]
    public void timeout_types_that_replace()
    {
        SagaTimeouts.Replaces(typeof(FeedingTime)).ShouldBeTrue();
        SagaTimeouts.Replaces(typeof(KennelInspection)).ShouldBeTrue();
        SagaTimeouts.Replaces(typeof(KennelClosingTime)).ShouldBeFalse();
        SagaTimeouts.KeyFor(new KennelInspection(Guid.NewGuid())).ShouldBe(typeof(KennelInspection).FullName);
    }
}

public record OpenKennel(Guid KennelSagaId);

public record WalkDog(Guid KennelSagaId);

public record MoveFeeding(Guid KennelSagaId, int Slot);

// Shares its key with nothing else; a TimeoutKey override names the key
public record FeedingTime(Guid KennelSagaId, int Slot) : TimeoutMessage(1.Hours())
{
    // protected internal only because CoreTests sees Wolverine's internals; an application overrides it as protected
    protected internal override string? TimeoutKey => "feeding";
}

[ReplacesEarlierTimeouts]
public record KennelInspection(Guid KennelSagaId) : TimeoutMessage(1.Days());

public record KennelClosingTime(Guid KennelSagaId) : TimeoutMessage(1.Days());

public record DogWalked(Guid KennelSagaId);

// Only in the hosts these tests build: CoreTests' shared hosts scan this assembly
[WolverineIgnore]
public class KennelSaga : Saga
{
    public Guid Id { get; set; }
    public List<int> Feedings { get; set; } = [];

    public static (KennelSaga, FeedingTime) Start(OpenKennel command)
        => (new KennelSaga { Id = command.KennelSagaId }, new FeedingTime(command.KennelSagaId, 1));

    public DogWalked Handle(WalkDog command) => new(Id);

    public FeedingTime Handle(MoveFeeding command) => new(Id, command.Slot);

    public void Handle(FeedingTime feeding) => Feedings.Add(feeding.Slot);

    public void Handle(KennelInspection inspection) { }

    public void Handle(KennelClosingTime closing) { }

    public void Compensate(WalkDog failed, ExceptionInfo ex) { }
}

public record OpenStrictKennel(Guid StrictKennelSagaId);

public record FeedDog(Guid StrictKennelSagaId);

[DiscardIfSagaMissing]
public record GroomDog(Guid StrictKennelSagaId);

[WolverineIgnore]
[SagaMissing(SagaMissingBehavior.Throw)]
public class StrictKennelSaga : Saga
{
    public Guid Id { get; set; }

    public static StrictKennelSaga Start(OpenStrictKennel command) => new() { Id = command.StrictKennelSagaId };

    public void Handle(FeedDog command) { }

    public void Handle(GroomDog command) { }
}
