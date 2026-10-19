using Bobcat.Engine;
using JasperFx.Descriptors;
using JasperFx.Events;
using JasperFx.Events.Descriptors;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace Bobcat.CritterStack.ScenarioTests;

// bobcat#672: ThenSingleReadModel<T>() refuses a multi-stream projection before bobcat#616's no-stream reading, so
// the misuse is a SpecCriticalException rather than a wrong about how many T the store holds. A fake store, no database.

public class single_read_model_kind
{
    /// <summary>A view projected across streams.</summary>
    public class CustomerRollup
    {
        public Guid Id { get; set; }
    }

    [Fact]
    public async Task a_multi_stream_projection_is_refused_even_when_the_scenario_has_no_stream()
    {
        var store = new FakeEventStore("main") { Usage = new EventStoreUsage() };
        store.Usage.Subscriptions.Add(new SubscriptionDescriptor(SubscriptionType.MultiStreamProjection)
        {
            AggregateType = TypeDescriptor.For(typeof(CustomerRollup))
        });

        var services = new ServiceCollection();
        services.AddSingleton<IEventStore>(store);
        var scenario = new CritterStackScenario(services.BuildServiceProvider());

        var refused = await Should.ThrowAsync<SpecCriticalException>(() => scenario.ThenSingleReadModel<CustomerRollup>());
        refused.Message.ShouldContain("reads a single-stream projection, but CustomerRollup is a MultiStreamProjection");
        refused.Message.ShouldContain("ThenReadModel<CustomerRollup>(id, ...)");
    }
}
