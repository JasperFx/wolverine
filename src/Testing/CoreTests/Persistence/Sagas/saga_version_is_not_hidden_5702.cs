using JasperFx;
using JasperFx.Core.Reflection;
using Microsoft.Extensions.Hosting;
using Wolverine;
using Wolverine.Attributes;
using Wolverine.Persistence.Sagas;
using Wolverine.Tracking;
using Xunit;

namespace CoreTests.Persistence.Sagas;

// GH-5702. Saga.Version is the revision every saga store checks for concurrent updates. A saga type that declares its
// own Version hides it, and the check quietly reads the wrong property, so Wolverine refuses the saga at startup
public class saga_version_is_not_hidden_5702
{
    [Fact]
    public async Task a_saga_with_its_own_version_is_refused_at_startup()
    {
        var ex = await Should.ThrowAsync<InvalidSagaException>(async () =>
        {
            using var host = await Host.CreateDefaultBuilder()
                .UseWolverine(opts => opts.Discovery.DisableConventionalDiscovery().IncludeType<ShipmentWithItsOwnVersion>())
                .StartAsync(TestContext.Current.CancellationToken);
        });

        // The message names the saga type and both fixes
        ex.Message.ShouldContain(typeof(ShipmentWithItsOwnVersion).FullNameInCode());
        ex.Message.ShouldContain("hides Saga.Version");
        ex.Message.ShouldContain("Remove it");
        ex.Message.ShouldContain("needs nothing of its own");
        ex.Message.ShouldContain("rename it");
    }

    [Fact]
    public async Task a_version_declared_on_a_base_class_of_the_saga_is_refused_too()
    {
        var ex = await Should.ThrowAsync<InvalidSagaException>(async () =>
        {
            using var host = await Host.CreateDefaultBuilder()
                .UseWolverine(opts => opts.Discovery.DisableConventionalDiscovery().IncludeType<ExpressShipment>())
                .StartAsync(TestContext.Current.CancellationToken);
        });

        ex.Message.ShouldContain(typeof(ExpressShipment).FullNameInCode());
        ex.Message.ShouldContain(typeof(ShipmentBase).FullNameInCode());
    }

    [Fact]
    public async Task an_irevisioned_saga_needs_no_version_of_its_own()
    {
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts => opts.Discovery.DisableConventionalDiscovery().IncludeType<RevisionedShipment>())
            .StartAsync(TestContext.Current.CancellationToken);

        await host.InvokeMessageAndWaitAsync(new StartRevisionedShipment(Guid.NewGuid()));
    }
}

public record StartShipmentWithItsOwnVersion(Guid Id);

// Refused at startup, so kept out of conventional discovery: every CoreTests host scanning this assembly would fail
[WolverineIgnore]
public class ShipmentWithItsOwnVersion : Saga
{
    public Guid Id { get; set; }

#pragma warning disable CS0108
    public string? Version { get; set; }
#pragma warning restore CS0108

    public static ShipmentWithItsOwnVersion Start(StartShipmentWithItsOwnVersion command) => new() { Id = command.Id };
}

[WolverineIgnore]
public abstract class ShipmentBase : Saga
{
    public Guid Id { get; set; }

#pragma warning disable CS0108
    public new int Version { get; set; }
#pragma warning restore CS0108
}

public record StartExpressShipment(Guid Id);

[WolverineIgnore]
public class ExpressShipment : ShipmentBase
{
    public static ExpressShipment Start(StartExpressShipment command) => new() { Id = command.Id };
}

public record StartRevisionedShipment(Guid Id);

// IRevisioned's Version is Saga.Version: nothing to declare
[WolverineIgnore]
public class RevisionedShipment : Saga, IRevisioned
{
    public Guid Id { get; set; }

    public static RevisionedShipment Start(StartRevisionedShipment command) => new() { Id = command.Id };
}
