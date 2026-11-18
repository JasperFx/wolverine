using Microsoft.Extensions.Hosting;
using Wolverine.Attributes;
using Wolverine.Configuration.Capabilities;
using Wolverine.Persistence.Sagas;
using Wolverine.Runtime;
using Wolverine.Tracking;
using Xunit;

namespace CoreTests.Persistence.Sagas;

// GH-5672: the rules around a Compensate method for an interface or a base type, on in-memory sagas. The story --
// a transfer whose steps all implement ITransferStep -- is told by the SagaWorkflows specifications
public class saga_compensate_a_family_5672
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

    private static Type[] compensated(IHost host, Type sagaType)
        => host.GetRuntime().Options.HandlerGraph.SagaChains()
            .Where(x => x.SagaType == sagaType)
            .Select(x => SagaCompensations.FaultMessageType(x.MessageType))
            .OfType<Type>()
            .OrderBy(x => x.Name, StringComparer.Ordinal)
            .ToArray();

    [Fact]
    public async Task an_interface_method_covers_every_known_concrete_type_but_not_the_sagas_own_messages()
    {
        using var host = await hostAsync(null, typeof(ShipmentSaga), typeof(CarrierHandler));

        // PackParcel is returned by the saga, LoadTruck is handled by a handler, and ShipmentArrived implements the
        // interface too but is a message the saga handles itself
        compensated(host, typeof(ShipmentSaga)).ShouldBe([typeof(IShipmentStep), typeof(LoadTruck), typeof(PackParcel)]);
    }

    [Fact]
    public async Task a_registered_message_type_is_known()
    {
        using var host = await hostAsync(opts => opts.RegisterMessageType(typeof(InsureParcel)), typeof(ShipmentSaga),
            typeof(CarrierHandler));

        compensated(host, typeof(ShipmentSaga)).ShouldContain(typeof(InsureParcel));
    }

    [Fact]
    public async Task Compensates_names_a_type_nothing_else_reveals()
    {
        using var host = await hostAsync(null, typeof(NamedShipmentSaga));

        compensated(host, typeof(NamedShipmentSaga)).ShouldBe([typeof(IShipmentStep), typeof(InsureParcel)]);
    }

    [Fact]
    public async Task a_catch_all_covers_only_what_the_saga_returns()
    {
        using var host = await hostAsync(null, typeof(CatchAllShipmentSaga), typeof(CarrierHandler));

        compensated(host, typeof(CatchAllShipmentSaga)).ShouldBe([typeof(object), typeof(PackParcel)]);
    }

    [Fact]
    public async Task a_saga_that_handles_a_fault_itself_keeps_that_handler()
    {
        using var host = await hostAsync(null, typeof(HandsOnShipmentSaga), typeof(CarrierHandler));

        // Fault<LoadTruck> is handled by hand, so the interface method is not filed under it as well
        var chain = host.GetRuntime().Options.HandlerGraph.SagaChains()
            .Single(x => x.MessageType == typeof(Fault<LoadTruck>));
        chain.SagaHandlerCalls().Select(x => x.Method.Name).ShouldBe(["Handle"]);
    }

    [Fact]
    public async Task Compensates_refuses_a_type_the_method_cannot_take()
    {
        var e = await Should.ThrowAsync<InvalidSagaException>(() => hostAsync(null, typeof(MisnamedShipmentSaga)));
        e.Message.ShouldContain("cannot be passed as its failed message");
    }

    [Fact]
    public async Task the_saga_descriptor_lists_what_each_Compensate_covers()
    {
        using var host = await hostAsync(null, typeof(ShipmentSaga), typeof(CarrierHandler));

        var capabilities = await ServiceCapabilities.ReadFrom(host.GetRuntime(), null, TestContext.Current.CancellationToken);
        var saga = capabilities.Sagas.Single(x => x.StateType.FullName == typeof(ShipmentSaga).FullName);

        var compensation = saga.Compensations.ShouldHaveSingleItem();
        compensation.FailedMessageType.Name.ShouldBe(nameof(IShipmentStep));
        compensation.Covers.Select(x => x.Name).ShouldBe(["LoadTruck", "PackParcel"]);

        // The flow diagram draws a failure edge from each concrete message the method covers
        var mermaid = SagaFlowDiagram.ToMermaid(saga);
        mermaid.ShouldContain("msg_LoadTruck -.->|\"fails\"| Compensate_IShipmentStep_1");
        mermaid.ShouldContain("msg_PackParcel -.->|\"fails\"| Compensate_IShipmentStep_1");
    }
}

public interface IShipmentStep
{
    Guid ShipmentSagaId { get; }
}

public record StartShipment(Guid ShipmentSagaId);

public record PackParcel(Guid ShipmentSagaId) : IShipmentStep;

public record LoadTruck(Guid ShipmentSagaId) : IShipmentStep;

public record InsureParcel(Guid ShipmentSagaId) : IShipmentStep;

public record ShipmentArrived(Guid ShipmentSagaId) : IShipmentStep;

public record ShipmentReturned(Guid ShipmentSagaId);

// Only in the hosts these tests build: CoreTests' shared hosts scan this assembly
[WolverineIgnore]
public static class CarrierHandler
{
    public static void Handle(PackParcel command) { }
    public static void Handle(LoadTruck command) { }
    public static void Handle(ShipmentReturned returned) { }
}

// Only in the hosts these tests build: CoreTests' shared hosts scan this assembly
[WolverineIgnore]
public class ShipmentSaga : CompensatingSaga
{
    public Guid Id { get; set; }

    public static (ShipmentSaga, PackParcel) Start(StartShipment command)
        => (new ShipmentSaga { Id = command.ShipmentSagaId }, new PackParcel(command.ShipmentSagaId));

    public void Handle(ShipmentArrived arrived) => MarkCompleted();

    public ShipmentReturned Compensate(IShipmentStep failed, ExceptionInfo ex) => new(Id);
}

public record StartNamedShipment(Guid NamedShipmentSagaId);

// Only in the hosts these tests build: CoreTests' shared hosts scan this assembly
[WolverineIgnore]
public class NamedShipmentSaga : Saga
{
    public Guid Id { get; set; }

    public static NamedShipmentSaga Start(StartNamedShipment command) => new() { Id = command.NamedShipmentSagaId };

    [Compensates(typeof(InsureParcel))]
    public void Compensate(IShipmentStep failed, ExceptionInfo ex) { }
}

public record StartCatchAllShipment(Guid CatchAllShipmentSagaId);

// Only in the hosts these tests build: CoreTests' shared hosts scan this assembly
[WolverineIgnore]
public class CatchAllShipmentSaga : Saga
{
    public Guid Id { get; set; }

    public static (CatchAllShipmentSaga, PackParcel) Start(StartCatchAllShipment command)
        => (new CatchAllShipmentSaga { Id = command.CatchAllShipmentSagaId }, new PackParcel(command.CatchAllShipmentSagaId));

    public void Compensate(object failed, ExceptionInfo ex) { }
}

public record StartHandsOnShipment(Guid HandsOnShipmentSagaId);

// Only in the hosts these tests build: CoreTests' shared hosts scan this assembly
[WolverineIgnore]
public class HandsOnShipmentSaga : Saga
{
    public Guid Id { get; set; }

    public static (HandsOnShipmentSaga, LoadTruck) Start(StartHandsOnShipment command)
        => (new HandsOnShipmentSaga { Id = command.HandsOnShipmentSagaId }, new LoadTruck(command.HandsOnShipmentSagaId));

    public void Handle(Fault<LoadTruck> fault) { }

    public void Compensate(IShipmentStep failed, ExceptionInfo ex) { }
}

public record StartMisnamedShipment(Guid MisnamedShipmentSagaId);

// Only in the hosts these tests build: CoreTests' shared hosts scan this assembly
[WolverineIgnore]
public class MisnamedShipmentSaga : Saga
{
    public Guid Id { get; set; }

    public static MisnamedShipmentSaga Start(StartMisnamedShipment command) => new() { Id = command.MisnamedShipmentSagaId };

    [Compensates(typeof(ShipmentReturned))]
    public void Compensate(IShipmentStep failed, ExceptionInfo ex) { }
}
